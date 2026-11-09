using System.Text.Json;
using SagaFlow.Core.Messages;
using SagaFlow.Core.State;
using SagaFlow.Engine.Coordination;
using SagaFlow.Engine.Messaging;

namespace SagaFlow.Engine.Tests;

public sealed class OutcomeStateRecoveryTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CommandSnapshotRetainsHistoryButContinuationRequiresAuthoritativeState(int lostAtStep)
    {
        var definition = CoordinationTestFixture.Linear(count: 3);
        var original = new CoordinationTestFixture(definition);
        var id = await original.Coordinator.StartAsync(new { customer = "history-customer" });
        for (var step = 0; step < lostAtStep; step++)
        {
            var prior = original.Bus.Commands.Last();
            await original.Receiver.ReceiveAsync(original.Outcome(prior, result: new { completedStep = step }));
        }
        var current = original.Bus.Commands.Last();
        Assert.NotNull(current.RecoverySnapshot);
        var recovered = new CoordinationTestFixture(definition);
        var outcome = new SagaOutcomeEvent
        {
            Headers = current.Headers, CausationId = current.Headers.CommandId,
            RecoverySnapshot = current.RecoverySnapshot, Name = "WorkCompleted", IsSuccess = true,
            Result = JsonSerializer.SerializeToElement(new { completedStep = lostAtStep })
        };

        var captured = Assert.IsType<SagaInstance>(SagaExecutionSnapshotCodec.Restore(current.RecoverySnapshot, current.Headers, definition));
        Assert.Equal(current.Headers, captured.Steps[lostAtStep].Execution);
        Assert.Equal(lostAtStep, captured.Steps.Count(step => step.Status == SagaStepStatus.Completed));
        for (var step = 0; step < lostAtStep; step++)
        {
            Assert.Equal(step, captured.Steps[step].Result!.Value.GetProperty("completedStep").GetInt32());
            Assert.NotNull(captured.Steps[step].Execution);
        }
        var error = await Assert.ThrowsAsync<SagaOutcomeRecoveryRequiredException>(() => recovered.Receiver.ReceiveAsync(outcome));
        Assert.Contains("cannot prove", error.Message);
        Assert.Null(await recovered.Store.FindAsync(id.ToString("N")));
        Assert.Empty(recovered.Bus.Commands);

        var checkpoint = await original.State(id);
        checkpoint.Revision = 0;
        await recovered.Store.SaveAsync(checkpoint);
        await recovered.Receiver.ReceiveAsync(outcome);

        var state = await recovered.State(id);
        Assert.Equal("history-customer", state.Context.GetProperty("customer").GetString());
        Assert.Equal(lostAtStep + 1, state.Steps.Count(step => step.Status == SagaStepStatus.Completed));
        for (var step = 0; step <= lostAtStep; step++)
        {
            Assert.Equal(step, state.Steps[step].Result!.Value.GetProperty("completedStep").GetInt32());
            Assert.NotNull(state.Steps[step].Execution);
        }
        Assert.All(state.Steps.Skip(lostAtStep + 1), step => Assert.NotEqual(SagaStepStatus.Unknown, step.Status));
        if (lostAtStep == 2)
        {
            Assert.Equal(SagaStatus.Completed, state.Status);
            Assert.Empty(recovered.Bus.Commands);
        }
        else
        {
            var next = Assert.Single(recovered.Bus.Commands);
            Assert.Equal($"step-{lostAtStep + 1}", next.Headers.StepId);
            Assert.NotNull(next.RecoverySnapshot);
            Assert.Equal(SagaStatus.Running, state.Status);
        }
        await Assert.ThrowsAsync<DuplicateSagaOutcomeException>(() => recovered.Receiver.ReceiveAsync(outcome));
        Assert.Equal(lostAtStep == 2 ? 0 : 1, recovered.Bus.Commands.Count);
    }

    [Fact]
    public async Task IncomingTargetRequiresAuthoritativeStateToResumeItsOriginalClaim()
    {
        var definition = CoordinationTestFixture.Linear();
        var original = new CoordinationTestFixture(definition);
        var identity = new ProcessInvocationIdentity(Guid.NewGuid().ToString("N"), "parent-correlation",
            new("parent", "source", "1.0"), Guid.NewGuid().ToString("N"), "invoke",
            new(definition.Domain, definition.Name, definition.Version));
        var message = new InvocationMessage(identity, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        var request = new ProcessInvocationRequest(identity, JsonSerializer.SerializeToElement(new { child = true }), message);
        var created = await original.Store.GetOrCreateIncomingAsync(request,
            () => original.Instances.Create(definition, new { customer = "incoming" }), DateTimeOffset.UtcNow);
        await original.Coordinator.StartIncomingAsync(created.Id);
        var command = Assert.Single(original.Bus.Commands);
        var captured = Assert.IsType<SagaInstance>(SagaExecutionSnapshotCodec.Restore(command.RecoverySnapshot, command.Headers, definition));
        Assert.Equal(identity, captured.IncomingInvocation!.Identity);
        Assert.Equal(message, captured.IncomingInvocation.RequestMessage);
        var recovered = new CoordinationTestFixture(definition);
        var outcome = new SagaOutcomeEvent
        {
            Headers = command.Headers, CausationId = command.Headers.CommandId,
            RecoverySnapshot = command.RecoverySnapshot, Name = "WorkCompleted", IsSuccess = true,
            Result = JsonSerializer.SerializeToElement(new { accepted = true })
        };

        var error = await Assert.ThrowsAsync<SagaOutcomeRecoveryRequiredException>(() => recovered.Receiver.ReceiveAsync(outcome));
        Assert.Contains("cannot prove", error.Message);
        Assert.Null(await recovered.Store.FindAsync(created.Id));
        Assert.Empty(recovered.Bus.Commands);

        var checkpoint = (await original.Store.FindAsync(created.Id))!;
        checkpoint.Revision = 0;
        Assert.True(await recovered.Store.TryRecoverIncomingAsync(checkpoint));
        await recovered.Receiver.ReceiveAsync(outcome);

        var state = (await recovered.Store.FindAsync(created.Id))!;
        Assert.Equal(identity, state.IncomingInvocation!.Identity);
        Assert.Equal(message, state.IncomingInvocation.RequestMessage);
        Assert.Equal(SagaStepStatus.Completed, state.Steps[0].Status);
        Assert.Single(recovered.Bus.Commands);
        var duplicate = await recovered.Store.GetOrCreateIncomingAsync(request,
            () => throw new Exception("Duplicate claim must not create another Saga."), DateTimeOffset.UtcNow);
        Assert.Equal(created.Id, duplicate.Id);
    }

    [Fact]
    public async Task CompensationOutcomeRequiresAuthoritativeStateToResumeRecordedProgress()
    {
        var definition = CoordinationTestFixture.Linear();
        var original = new CoordinationTestFixture(definition);
        var id = await original.Coordinator.StartAsync(new { customer = "compensation" });
        await original.Receiver.ReceiveAsync(original.Outcome(original.Bus.Commands.Last()));
        await original.Receiver.ReceiveAsync(original.Outcome(original.Bus.Commands.Last(), success: false));
        var command = original.Bus.Commands.Last();
        Assert.True(command.Headers.IsCompensation);
        var captured = Assert.IsType<SagaInstance>(SagaExecutionSnapshotCodec.Restore(command.RecoverySnapshot, command.Headers, definition));
        Assert.Equal(SagaStepStatus.Completed, captured.Steps[0].Status);
        Assert.Equal(SagaStepStatus.Failed, captured.Steps[1].Status);
        var capturedCompensation = Assert.Single(captured.Compensations);
        Assert.Equal(SagaCompensationStatus.Dispatched, capturedCompensation.Status);
        Assert.Equal(command.Headers.ExecutionId, capturedCompensation.Execution!.ExecutionId);
        var recovered = new CoordinationTestFixture(definition);
        var outcome = new SagaOutcomeEvent
        {
            Headers = command.Headers, CausationId = command.Headers.CommandId,
            RecoverySnapshot = command.RecoverySnapshot, Name = "UndoCompleted", IsSuccess = true,
            Result = JsonSerializer.SerializeToElement(new { undone = true })
        };

        var error = await Assert.ThrowsAsync<SagaOutcomeRecoveryRequiredException>(() => recovered.Receiver.ReceiveAsync(outcome));
        Assert.Contains("cannot prove", error.Message);
        Assert.Null(await recovered.Store.FindAsync(id.ToString("N")));
        Assert.Empty(recovered.Bus.Commands);

        var checkpoint = await original.State(id);
        checkpoint.Revision = 0;
        await recovered.Store.SaveAsync(checkpoint);
        await recovered.Receiver.ReceiveAsync(outcome);

        var state = await recovered.State(id);
        Assert.Equal(SagaStepStatus.Completed, state.Steps[0].Status);
        Assert.Equal(SagaStepStatus.Failed, state.Steps[1].Status);
        var compensation = Assert.Single(state.Compensations);
        Assert.Equal(SagaCompensationStatus.Completed, compensation.Status);
        Assert.Equal(command.Headers.ExecutionId, compensation.Execution!.ExecutionId);
        Assert.Empty(recovered.Bus.Commands);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task CorruptOrMismatchedSnapshotCannotCreateAnExecutableSaga(int defect)
    {
        var definition = CoordinationTestFixture.Linear();
        var original = new CoordinationTestFixture(definition);
        var id = await original.Coordinator.StartAsync(new { });
        var command = Assert.Single(original.Bus.Commands);
        var recovered = new CoordinationTestFixture(definition);
        var snapshot = defect switch
        {
            0 => new byte[] { 1, 2, 3 },
            2 => System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(command.RecoverySnapshot!)
                .Replace("\"Version\":1", "\"Version\":2", StringComparison.Ordinal)),
            _ => command.RecoverySnapshot!
        };
        var outcome = new SagaOutcomeEvent
        {
            Headers = defect switch
            {
                1 => command.Headers with { ExecutionId = Guid.NewGuid().ToString("N") },
                3 => command.Headers with { ProcessVersion = "2.0" },
                _ => command.Headers
            },
            CausationId = command.Headers.CommandId, RecoverySnapshot = snapshot,
            Name = "WorkCompleted", IsSuccess = true, Result = JsonSerializer.SerializeToElement(new { accepted = true })
        };

        var error = await Assert.ThrowsAsync<SagaOutcomeRecoveryRequiredException>(() => recovered.Receiver.ReceiveAsync(outcome));
        Assert.DoesNotContain("cannot prove", error.Message);
        Assert.Null(await recovered.Store.FindAsync(id.ToString("N")));
        Assert.Empty(recovered.Bus.Commands);
    }

    [Fact]
    public async Task InMemoryRecoveryRetainsEvidenceWithoutReapplyingAnOutcomePreviouslyProcessed()
    {
        var definition = CoordinationTestFixture.Linear(count: 3);
        var original = new CoordinationTestFixture(definition);
        var id = await original.Coordinator.StartAsync(new { context = "original" });
        await original.Receiver.ReceiveAsync(original.Outcome(original.Bus.Commands.Single()));
        var reportedCommand = original.Bus.Commands.Last();
        var restarted = new CoordinationTestFixture(definition);
        using (var document = JsonDocument.Parse("""{"approved": true}"""))
        {
            var outcome = new SagaOutcomeEvent
            {
                Headers = reportedCommand.Headers, CausationId = reportedCommand.Headers.CommandId,
                Name = "WorkCompleted", IsSuccess = true, Result = document.RootElement
            };
            await original.Receiver.ReceiveAsync(outcome);
            Assert.Equal(definition.Steps[2].Name, (await original.State(id)).CurrentStep);
            await Assert.ThrowsAsync<SagaOutcomeRecoveryRequiredException>(() => restarted.Receiver.ReceiveAsync(outcome));
            await Assert.ThrowsAsync<SagaOutcomeRecoveryRequiredException>(() => restarted.Receiver.ReceiveAsync(outcome));
        }

        var state = await restarted.State(id);
        Assert.Equal(SagaStatus.RecoveryRequired, state.Status);
        Assert.Equal(1, state.Revision);
        Assert.All(state.Steps, step => Assert.Equal(SagaStepStatus.Unknown, step.Status));
        Assert.Null(state.CurrentStep);
        Assert.Equal(reportedCommand.Headers, state.RecoveryOutcome!.Headers);
        Assert.Equal(reportedCommand.Headers, state.Steps[1].Execution);
        Assert.True(state.RecoveryOutcome.Result.GetProperty("approved").GetBoolean());
        Assert.Null(state.Steps[0].Execution);
        Assert.Empty(restarted.Bus.Commands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveredSnapshotClassifiesConflictingResultOrIdentityForPreservation(bool identityConflict)
    {
        var definition = CoordinationTestFixture.Linear();
        var original = new CoordinationTestFixture(definition);
        var id = await original.Coordinator.StartAsync(new { });
        var headers = original.Bus.Commands.Single().Headers;
        var restarted = new CoordinationTestFixture(definition);
        var first = new SagaOutcomeEvent
        {
            Headers = headers, CausationId = headers.CommandId, Name = "WorkCompleted", IsSuccess = true
        };
        var initial = await Assert.ThrowsAsync<SagaOutcomeRecoveryRequiredException>(() => restarted.Receiver.ReceiveAsync(first));
        Assert.False(initial.IsConflictingEvidence);

        var conflicting = new SagaOutcomeEvent
        {
            Headers = identityConflict ? headers with { CorrelationId = "another-correlation" } : headers,
            CausationId = headers.CommandId, Name = first.Name, IsSuccess = identityConflict
        };
        var error = await Assert.ThrowsAsync<SagaOutcomeRecoveryRequiredException>(() => restarted.Receiver.ReceiveAsync(conflicting));
        Assert.True(error.IsConflictingEvidence);

        var state = await restarted.State(id);
        Assert.Equal(SagaStatus.RecoveryRequired, state.Status);
        Assert.Equal(1, state.Revision);
        Assert.Equal(first.MessageId, state.RecoveryOutcome!.MessageId);
        Assert.Equal(headers, state.RecoveryOutcome.Headers);
        Assert.True(state.RecoveryOutcome.IsSuccess);
        Assert.Empty(restarted.Bus.Commands);
    }

    [Fact]
    public async Task MissingStateWithConflictingCausationCannotCreateARecoverySnapshot()
    {
        var definition = CoordinationTestFixture.Linear();
        var original = new CoordinationTestFixture(definition);
        var id = await original.Coordinator.StartAsync(new { });
        var restarted = new CoordinationTestFixture(definition);
        var outcome = new SagaOutcomeEvent
        {
            Headers = original.Bus.Commands.Single().Headers, CausationId = "foreign", Name = "WorkCompleted",
            IsSuccess = true
        };

        await Assert.ThrowsAsync<InvalidSagaOutcomeException>(() => restarted.Receiver.ReceiveAsync(outcome));
        Assert.Null(await restarted.Store.FindAsync(id.ToString("N")));
        Assert.Empty(restarted.Bus.Commands);
    }
}
