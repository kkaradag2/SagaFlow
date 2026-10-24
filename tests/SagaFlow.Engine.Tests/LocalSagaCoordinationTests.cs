using System.Text.Json;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Definitions;
using SagaFlow.Core.Messages;
using SagaFlow.Core.State;
using SagaFlow.Engine.Coordination;
using SagaFlow.Engine.Messaging;

namespace SagaFlow.Engine.Tests;

public sealed class LocalSagaCoordinationTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(10)]
    public async Task CreatesDynamicPendingStepsAndDispatchesOnlyExplicitInitialStep(int count)
    {
        var definition = CoordinationTestFixture.Linear(count, reverseYamlOrder: true);
        var fixture = new CoordinationTestFixture(definition);
        var initial = fixture.Instances.Create(definition, new { orderId = 42 });
        Assert.All(initial.Steps, step => { Assert.Equal(SagaStepStatus.Pending, step.Status); Assert.Null(step.ExecutionOrder); });
        var id = await fixture.Coordinator.StartAsync(new { orderId = 42, nested = new { total = 17 } });
        var state = await fixture.State(id);
        Assert.Equal(count, state.Steps.Count);
        Assert.Equal(count, state.Compensations.Count);
        Assert.Equal(SagaStatus.Running, state.Status);
        Assert.Equal("step-0", state.CurrentStep);
        Assert.True(Guid.TryParse(state.CorrelationId, out _));
        Assert.Equal(("domain", "process", "1.0", "Created"), (state.DefinitionDomain, state.DefinitionName, state.DefinitionVersion, state.TriggerEvent));
        Assert.Equal(42, state.Context.GetProperty("orderId").GetInt32());
        Assert.True(state.CreatedAt <= state.UpdatedAt);
        var command = Assert.Single(fixture.Bus.Commands);
        Assert.Equal("Command0", command.Name);
        Assert.Equal(state.Id, command.Headers.SagaInstanceId);
        Assert.Equal(state.CorrelationId, command.Headers.CorrelationId);
        Assert.Equal("step-0", command.Headers.StepId);
        Assert.Equal(42, command.Payload.GetProperty("orderId").GetInt32());
        var dispatched = state.Steps.Single(s => s.StepId == "step-0");
        Assert.Equal(SagaStepStatus.Dispatched, dispatched.Status);
        Assert.Equal(1, dispatched.ExecutionOrder);
        Assert.Equal(command.Payload.GetRawText(), dispatched.Input!.Value.GetRawText());
        Assert.NotNull(dispatched.StartedAt);
        Assert.All(state.Steps.Where(s => s.StepId != "step-0"), s => Assert.Equal(SagaStepStatus.Pending, s.Status));
        Assert.All(state.Compensations, c => Assert.Equal(SagaCompensationStatus.Pending, c.Status));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(10)]
    public async Task CompletesDifferentSizedFlowsInTransitionOrderRatherThanYamlOrder(int count)
    {
        var fixture = new CoordinationTestFixture(CoordinationTestFixture.Linear(count, reverseYamlOrder: true));
        var id = await fixture.Coordinator.StartAsync(new { value = 7 });
        for (var i = 0; i < count; i++)
            await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.ToArray()[i], result: new { done = i }));
        var state = await fixture.State(id);
        Assert.Equal(SagaStatus.Completed, state.Status);
        Assert.Null(state.CurrentStep);
        Assert.Equal(count, fixture.Bus.Commands.Count);
        foreach (var step in state.Steps)
        {
            var expectedOrder = int.Parse(step.StepId[5..]) + 1;
            Assert.Equal(expectedOrder, step.ExecutionOrder);
            Assert.Equal(SagaStepStatus.Completed, step.Status);
            Assert.Equal(expectedOrder - 1, step.Result!.Value.GetProperty("done").GetInt32());
            Assert.NotNull(step.CompletedAt);
        }
    }

    [Fact]
    public async Task FailurePlansOnlySuccessfulCompensatableStepsInReverseActualOrder()
    {
        var fixture = new CoordinationTestFixture(CoordinationTestFixture.Linear(4, reverseYamlOrder: true));
        var id = await fixture.Coordinator.StartAsync(new { id = 9 });
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.ToArray()[0]));
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.ToArray()[1]));
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.ToArray()[2], success: false));
        var state = await fixture.State(id);
        Assert.Equal(SagaStatus.CompensationRequested, state.Status);
        Assert.Null(state.CurrentStep);
        var plan = fixture.States.GetCompensationPlan(state);
        Assert.Equal(new[] { "step-1", "step-0" }, plan.Select(c => c.StepId));
        Assert.Equal(new[] { "Undo1", "Undo0" }, plan.Select(c => c.Command));
        Assert.Equal(new int?[] { 1, 2 }, plan.Select(c => c.ExecutionOrder));
        Assert.All(plan, c => { Assert.Null(c.StartedAt); Assert.Null(c.CompletedAt); });
        Assert.Equal(SagaStepStatus.Completed, state.Steps.Single(s => s.StepId == "step-0").Status);
        Assert.Equal(SagaStepStatus.Failed, state.Steps.Single(s => s.StepId == "step-2").Status);
        Assert.Equal(SagaStepStatus.Pending, state.Steps.Single(s => s.StepId == "step-3").Status);
        Assert.Equal(3, fixture.Bus.Commands.Count);
        Assert.DoesNotContain(fixture.Bus.Commands, c => c.Name.StartsWith("Undo", StringComparison.Ordinal));
        plan[0].Status = SagaCompensationStatus.Pending;
        Assert.Equal(SagaCompensationStatus.Planned, (await fixture.State(id)).Compensations.Single(c => c.StepId == "step-1").Status);
    }

    [Fact]
    public async Task StepsWithoutCompensationDoNotCreateOrPublishCompensationCommands()
    {
        var fixture = new CoordinationTestFixture(CoordinationTestFixture.Linear(2, compensate: false));
        var id = await fixture.Coordinator.StartAsync(123);
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.ToArray()[0]));
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.ToArray()[1], success: false));
        var state = await fixture.State(id);
        Assert.Empty(state.Compensations);
        Assert.Empty(fixture.States.GetCompensationPlan(state));
        Assert.Equal(SagaStatus.CompensationRequested, state.Status);
        Assert.Equal(2, fixture.Bus.Commands.Count);
    }

    [Fact]
    public async Task CompensationPlanSkipsCompletedStepsWithoutACompensationDefinition()
    {
        var original = CoordinationTestFixture.Linear(3);
        var definition = new SagaDefinition
        {
            Name = original.Name, Domain = original.Domain, Version = original.Version, Trigger = original.Trigger, InitialStep = original.InitialStep,
            Steps = [original.Steps[2], new() { Name = "step-1", Command = "Command1", Transitions = original.Steps[1].Transitions }, original.Steps[0]]
        };
        var fixture = new CoordinationTestFixture(definition);
        var id = await fixture.Coordinator.StartAsync("payload");
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.ToArray()[0]));
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.ToArray()[1]));
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.ToArray()[2], success: false));
        var state = await fixture.State(id);
        Assert.Equal("Undo0", Assert.Single(fixture.States.GetCompensationPlan(state)).Command);
        Assert.Equal(SagaStepStatus.Completed, state.Steps.Single(s => s.StepId == "step-1").Status);
        Assert.DoesNotContain(state.Compensations, c => c.StepId == "step-1");
    }

    [Fact]
    public async Task RejectsDuplicateOutcomeWithoutAnotherTransitionOrPublication()
    {
        var fixture = new CoordinationTestFixture(CoordinationTestFixture.Linear());
        var id = await fixture.Coordinator.StartAsync(new { value = 1 });
        var outcome = fixture.Outcome(fixture.Bus.Commands.Single());
        await fixture.Receiver.ReceiveAsync(outcome);
        var before = await fixture.State(id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Receiver.ReceiveAsync(outcome));
        var after = await fixture.State(id);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(2, fixture.Bus.Commands.Count);
    }

    [Fact]
    public async Task DuplicateFailedOutcomeCannotReplanCompensation()
    {
        var fixture = new CoordinationTestFixture(CoordinationTestFixture.Linear());
        var id = await fixture.Coordinator.StartAsync("payload");
        var outcome = fixture.Outcome(fixture.Bus.Commands.Single(), success: false);
        await fixture.Receiver.ReceiveAsync(outcome);
        var before = await fixture.State(id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Receiver.ReceiveAsync(outcome));
        Assert.Equal(before.Revision, (await fixture.State(id)).Revision);
        Assert.Single(fixture.Bus.Commands);
    }

    [Theory]
    [InlineData("instance")]
    [InlineData("correlation")]
    [InlineData("unknown-step")]
    [InlineData("inactive-step")]
    public async Task RejectsMismatchedOutcomeMetadataAndLeavesStateUntouched(string mismatch)
    {
        var fixture = new CoordinationTestFixture(CoordinationTestFixture.Linear());
        var id = await fixture.Coordinator.StartAsync(new { id = 1 });
        var header = fixture.Bus.Commands.Single().Headers;
        var invalid = new SagaOutcomeEvent
        {
            IsSuccess = true, Headers = new()
            {
                SagaInstanceId = mismatch == "instance" ? Guid.NewGuid().ToString("N") : header.SagaInstanceId,
                CorrelationId = mismatch == "correlation" ? "wrong" : header.CorrelationId,
                StepId = mismatch switch { "unknown-step" => "unknown", "inactive-step" => "step-1", _ => header.StepId }
            }
        };
        var before = await fixture.State(id);
        if (mismatch == "instance") await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Receiver.ReceiveAsync(invalid));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Receiver.ReceiveAsync(invalid));
        Assert.Equal(before.Revision, (await fixture.State(id)).Revision);
        Assert.Single(fixture.Bus.Commands);
    }

    [Fact]
    public async Task RuntimeRevisitFailsDeterministicallyWithoutRedispatchingCompletedStep()
    {
        var fixture = new CoordinationTestFixture(CoordinationTestFixture.Linear(2));
        var id = await fixture.Coordinator.StartAsync("payload");
        // Cyclic definitions now fail validation. Simulate inconsistent persisted history instead
        // to retain coverage of the coordinator's independent once-only dispatch defense.
        await fixture.Store.UpdateAsync(id.ToString("N"), state =>
        {
            state.Steps.Single(s => s.StepId == "step-1").Status = SagaStepStatus.Completed;
            return true;
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.Single())));
        Assert.Contains("cycle/revisit", error.Message);
        var state = await fixture.State(id);
        Assert.Equal(SagaStatus.Failed, state.Status);
        Assert.All(state.Steps, s => Assert.Equal(SagaStepStatus.Completed, s.Status));
        Assert.Single(fixture.Bus.Commands);
        Assert.Single(state.Steps, s => s.StepId == "step-0");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicationFailureIsVisibleAndNeverLooksSuccessfullyDispatched(bool failSecondCommand)
    {
        var fixture = new CoordinationTestFixture(CoordinationTestFixture.Linear());
        fixture.Bus.OnPublish = (command, _) => command.Name == (failSecondCommand ? "Command1" : "Command0")
            ? ValueTask.FromException(new IOException("transport unavailable")) : ValueTask.CompletedTask;
        SagaDispatchException error;
        if (failSecondCommand)
        {
            await fixture.Coordinator.StartAsync("payload");
            error = await Assert.ThrowsAsync<SagaDispatchException>(() => fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.Single())));
        }
        else error = await Assert.ThrowsAsync<SagaDispatchException>(() => fixture.Coordinator.StartAsync("payload"));
        var state = (await fixture.Store.FindAsync(error.InstanceId))!;
        Assert.Equal(SagaStatus.Failed, state.Status);
        Assert.Null(state.CurrentStep);
        Assert.Contains("transport unavailable", state.FailureReason);
        Assert.Equal(SagaStepStatus.Failed, state.Steps.Single(s => s.StepId == error.StepId).Status);
        Assert.Equal(failSecondCommand ? 1 : 0, fixture.Bus.Commands.Count);
    }

    [Fact]
    public async Task MissingTransportFailsExplicitlyWithoutPretendingToPublish()
    {
        var fixture = new CoordinationTestFixture(CoordinationTestFixture.Linear());
        var coordinator = new SagaCoordinator(fixture.Registry, fixture.Store, fixture.Instances,
            new SagaStepExecutor(fixture.Store, new CommandDispatcher(), fixture.Clock), new(), fixture.States, fixture.Clock);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StartAsync(new { id = 1 }));
        Assert.Contains("No ISagaMessageBus transport adapter", error.Message);
        Assert.Empty(fixture.Bus.Commands);
    }

    [Fact]
    public async Task ConcurrentDuplicateOutcomesAdvanceOnce()
    {
        var fixture = new CoordinationTestFixture(CoordinationTestFixture.Linear());
        var id = await fixture.Coordinator.StartAsync("payload");
        var outcome = fixture.Outcome(fixture.Bus.Commands.Single());
        var attempts = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => Task.Run(async () =>
        {
            try { await fixture.Receiver.ReceiveAsync(outcome); return true; }
            catch (InvalidOperationException) { return false; }
        })));
        Assert.Equal(1, attempts.Count(success => success));
        Assert.Equal(2, fixture.Bus.Commands.Count);
        Assert.Equal("step-1", (await fixture.State(id)).CurrentStep);
    }

    [Fact]
    public async Task DifferentInstancesRemainIsolatedAndHaveUniqueIds()
    {
        var fixture = new CoordinationTestFixture(CoordinationTestFixture.Linear());
        var ids = await Task.WhenAll(Enumerable.Range(0, 25).Select(i => fixture.Coordinator.StartAsync(new { value = i })));
        Assert.Equal(ids.Length, ids.Distinct().Count());
        var first = await fixture.State(ids[0]);
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.Single(c => c.Headers.SagaInstanceId == first.Id)));
        for (var i = 0; i < ids.Length; i++)
        {
            var state = await fixture.State(ids[i]);
            Assert.Equal(i, state.Context.GetProperty("value").GetInt32());
            Assert.Equal(i == 0 ? "step-1" : "step-0", state.CurrentStep);
        }
    }

    [Fact]
    public async Task SynchronousOutcomeDuringPublicationDoesNotDeadlock()
    {
        var fixture = new CoordinationTestFixture(CoordinationTestFixture.Linear());
        fixture.Bus.OnPublish = async (command, token) => await fixture.Receiver.ReceiveAsync(fixture.Outcome(command), token);
        var id = await fixture.Coordinator.StartAsync("payload").WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(SagaStatus.Completed, (await fixture.State(id)).Status);
        Assert.Equal(2, fixture.Bus.Commands.Count);
    }

    [Fact]
    public async Task InvalidPayloadAndPrecancelledStartDoNotPublish()
    {
        var fixture = new CoordinationTestFixture(CoordinationTestFixture.Linear());
        var cyclic = new List<object>(); cyclic.Add(cyclic);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Coordinator.StartAsync(cyclic));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Coordinator.StartAsync("payload", new CancellationToken(true)));
        Assert.Empty(fixture.Bus.Commands);
    }

    [Fact]
    public async Task CanStartWithJsonDocumentPayloadAndNull()
    {
        var fixture = new CoordinationTestFixture(CoordinationTestFixture.Linear());
        Guid first;
        using (var document = JsonDocument.Parse("{\"value\":3}")) first = await fixture.Coordinator.StartAsync(document.RootElement);
        Assert.Equal(3, (await fixture.State(first)).Context.GetProperty("value").GetInt32());
        var second = await fixture.Coordinator.StartAsync<object?>(null);
        Assert.Equal(JsonValueKind.Null, (await fixture.State(second)).Context.ValueKind);
    }

    [Fact]
    public async Task CancellationDuringPublicationIsPersistedAsFailureAndExposesInstanceId()
    {
        var fixture = new CoordinationTestFixture(CoordinationTestFixture.Linear());
        using var cancellation = new CancellationTokenSource();
        fixture.Bus.OnPublish = (_, token) => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; };
        var error = await Assert.ThrowsAsync<SagaDispatchException>(() => fixture.Coordinator.StartAsync("payload", cancellation.Token));
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        var state = (await fixture.Store.FindAsync(error.InstanceId))!;
        Assert.Equal(SagaStatus.Failed, state.Status);
        Assert.Equal(SagaStepStatus.Failed, state.Steps.Single(s => s.StepId == "step-0").Status);
        Assert.Empty(fixture.Bus.Commands);
    }

    [Fact]
    public async Task StoreSeesAllPendingStepsBeforeFirstDispatchClaim()
    {
        var fixture = new CoordinationTestFixture(CoordinationTestFixture.Linear());
        var inspection = new InspectingStore(fixture.Store);
        var executor = new SagaStepExecutor(inspection, new CommandDispatcher(fixture.Bus), fixture.Clock);
        var coordinator = new SagaCoordinator(fixture.Registry, inspection, fixture.Instances, executor, new(), fixture.States, fixture.Clock);
        await coordinator.StartAsync("payload");
        Assert.True(inspection.SavedAllPending);
    }

    [Fact]
    public async Task CancellationAfterInitialSaveDoesNotLeaveAnOrphanedRunningInstance()
    {
        var fixture = new CoordinationTestFixture(CoordinationTestFixture.Linear());
        using var cancellation = new CancellationTokenSource();
        var inspection = new InspectingStore(fixture.Store) { AfterSave = cancellation.Cancel };
        var executor = new SagaStepExecutor(inspection, new CommandDispatcher(fixture.Bus), fixture.Clock);
        var coordinator = new SagaCoordinator(fixture.Registry, inspection, fixture.Instances, executor, new(), fixture.States, fixture.Clock);
        var error = await Assert.ThrowsAsync<SagaDispatchException>(() => coordinator.StartAsync("payload", cancellation.Token));
        var state = (await fixture.Store.FindAsync(error.InstanceId))!;
        Assert.Equal(SagaStatus.Failed, state.Status);
        Assert.All(state.Steps, s => Assert.Equal(SagaStepStatus.Pending, s.Status));
        Assert.Empty(fixture.Bus.Commands);
    }

    private sealed class InspectingStore(ISagaStateStore inner) : ISagaStateStore
    {
        public bool SavedAllPending { get; private set; }
        public Action? AfterSave { get; init; }
        public async ValueTask SaveAsync(SagaInstance instance, CancellationToken cancellationToken = default)
        {
            await inner.SaveAsync(instance, cancellationToken);
            SavedAllPending = (await inner.FindAsync(instance.Id, cancellationToken))!.Steps.All(s => s.Status == SagaStepStatus.Pending);
            AfterSave?.Invoke();
        }
        public ValueTask<SagaInstance?> FindAsync(string instanceId, CancellationToken cancellationToken = default) => inner.FindAsync(instanceId, cancellationToken);
        public ValueTask<TResult> UpdateAsync<TResult>(string instanceId, Func<SagaInstance, TResult> update, CancellationToken cancellationToken = default) => inner.UpdateAsync(instanceId, update, cancellationToken);
    }
}
