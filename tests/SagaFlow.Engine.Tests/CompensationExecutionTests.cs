using System.Text.Json;
using SagaFlow.Core.Messages;
using SagaFlow.Core.State;
using SagaFlow.Engine.Coordination;

namespace SagaFlow.Engine.Tests;

public sealed class CompensationExecutionTests
{
    [Fact]
    public async Task ExecutesCompensationsSequentiallyInReverseForwardOrder()
    {
        var fixture = new CoordinationTestFixture(CoordinationTestFixture.Linear(4));
        var id = await fixture.Coordinator.StartAsync(new { orderId = 42 });
        await SucceedForward(fixture, 0);
        await SucceedForward(fixture, 1);
        await SucceedForward(fixture, 2);

        var failedForward = fixture.Bus.Commands.ToArray()[3];
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(failedForward, success: false));

        var first = fixture.Bus.Commands.Last();
        AssertCompensation(first, "Undo2", "step-2");
        Assert.Equal(5, fixture.Bus.Commands.Count);
        var compensating = await fixture.State(id);
        Assert.Equal(SagaStatus.Compensating, compensating.Status);
        Assert.Equal(new[] { "step-2", "step-1", "step-0" }, compensating.Compensations.Select(item => item.StepId));
        Assert.Equal(new[] { SagaCompensationStatus.Dispatched, SagaCompensationStatus.Planned, SagaCompensationStatus.Planned },
            compensating.Compensations.Select(item => item.Status));

        await fixture.Receiver.ReceiveAsync(fixture.Outcome(first, result: new { released = 2 }));
        var second = fixture.Bus.Commands.Last();
        AssertCompensation(second, "Undo1", "step-1");
        Assert.Equal(6, fixture.Bus.Commands.Count);

        await fixture.Receiver.ReceiveAsync(fixture.Outcome(second, result: new { released = 1 }));
        var third = fixture.Bus.Commands.Last();
        AssertCompensation(third, "Undo0", "step-0");
        Assert.Equal(7, fixture.Bus.Commands.Count);

        await fixture.Receiver.ReceiveAsync(fixture.Outcome(third, result: new { released = 0 }));
        var completed = await fixture.State(id);
        Assert.Equal(SagaStatus.Compensated, completed.Status);
        Assert.All(completed.Compensations, item =>
        {
            Assert.Equal(SagaCompensationStatus.Completed, item.Status);
            Assert.NotNull(item.StartedAt);
            Assert.NotNull(item.CompletedAt);
            Assert.NotNull(item.Result);
        });
        Assert.Equal(new[] { SagaStepStatus.Completed, SagaStepStatus.Completed, SagaStepStatus.Completed, SagaStepStatus.Failed },
            completed.Steps.Select(step => step.Status));
    }

    [Fact]
    public async Task CompensationFailureStopsTheRemainingPlanAndPreservesForwardHistory()
    {
        var fixture = new CoordinationTestFixture(CoordinationTestFixture.Linear(3));
        var id = await fixture.Coordinator.StartAsync("payload");
        await SucceedForward(fixture, 0);
        await SucceedForward(fixture, 1);
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.ToArray()[2], success: false));
        var compensation = fixture.Bus.Commands.Last();

        await fixture.Receiver.ReceiveAsync(fixture.Outcome(compensation, success: false, result: new { error = "refund rejected" }));

        var state = await fixture.State(id);
        Assert.Equal(SagaStatus.Failed, state.Status);
        Assert.Equal(SagaCompensationStatus.Failed, state.Compensations[0].Status);
        Assert.Equal(SagaCompensationStatus.Planned, state.Compensations[1].Status);
        Assert.Equal(4, fixture.Bus.Commands.Count);
        Assert.Equal(new[] { SagaStepStatus.Completed, SagaStepStatus.Completed, SagaStepStatus.Failed },
            state.Steps.Select(step => step.Status));
    }

    [Fact]
    public async Task DuplicateCompensationOutcomeCannotDispatchAnotherCommand()
    {
        var fixture = new CoordinationTestFixture(CoordinationTestFixture.Linear(3));
        var id = await fixture.Coordinator.StartAsync("payload");
        await SucceedForward(fixture, 0);
        await SucceedForward(fixture, 1);
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.ToArray()[2], success: false));
        var first = fixture.Bus.Commands.Last();
        var outcome = fixture.Outcome(first);
        await fixture.Receiver.ReceiveAsync(outcome);
        var before = await fixture.State(id);

        await Assert.ThrowsAsync<DuplicateSagaOutcomeException>(() => fixture.Receiver.ReceiveAsync(outcome));

        Assert.Equal(before.Revision, (await fixture.State(id)).Revision);
        Assert.Equal(5, fixture.Bus.Commands.Count);
    }

    [Theory]
    [InlineData("correlation")]
    [InlineData("step")]
    [InlineData("kind")]
    public async Task MismatchedCompensationOutcomeLeavesStateUntouched(string mismatch)
    {
        var fixture = new CoordinationTestFixture(CoordinationTestFixture.Linear(2));
        var id = await fixture.Coordinator.StartAsync("payload");
        await SucceedForward(fixture, 0);
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.ToArray()[1], success: false));
        var command = fixture.Bus.Commands.Last();
        var invalid = new SagaOutcomeEvent
        {
            IsSuccess = true,
            Headers = new()
            {
                SagaInstanceId = command.Headers.SagaInstanceId,
                CorrelationId = mismatch == "correlation" ? "wrong" : command.Headers.CorrelationId,
                StepId = mismatch == "step" ? "step-1" : command.Headers.StepId,
                IsCompensation = mismatch != "kind"
            },
            Result = JsonSerializer.SerializeToElement(new { released = true })
        };
        var before = await fixture.State(id);

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => fixture.Receiver.ReceiveAsync(invalid));

        Assert.Equal(before.Revision, (await fixture.State(id)).Revision);
        Assert.Equal(3, fixture.Bus.Commands.Count);
    }

    [Fact]
    public async Task DuplicateForwardOutcomeCannotMasqueradeAsCompensationOutcome()
    {
        var fixture = new CoordinationTestFixture(CoordinationTestFixture.Linear(2));
        var id = await fixture.Coordinator.StartAsync("payload");
        var completedForward = fixture.Bus.Commands.Single();
        var completedOutcome = fixture.Outcome(completedForward);
        await fixture.Receiver.ReceiveAsync(completedOutcome);
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.ToArray()[1], success: false));
        var before = await fixture.State(id);

        await Assert.ThrowsAsync<DuplicateSagaOutcomeException>(() => fixture.Receiver.ReceiveAsync(completedOutcome));

        Assert.Equal(before.Revision, (await fixture.State(id)).Revision);
        Assert.Equal(SagaCompensationStatus.Dispatched, (await fixture.State(id)).Compensations.Single().Status);
    }

    [Fact]
    public async Task CompensationPublicationFailureIsPersistedAndDoesNotDispatchTheRest()
    {
        var fixture = new CoordinationTestFixture(CoordinationTestFixture.Linear(3));
        var id = await fixture.Coordinator.StartAsync("payload");
        await SucceedForward(fixture, 0);
        await SucceedForward(fixture, 1);
        fixture.Bus.OnPublish = (command, _) => command.Headers.IsCompensation
            ? ValueTask.FromException(new IOException("compensation transport unavailable"))
            : ValueTask.CompletedTask;

        await Assert.ThrowsAsync<SagaDispatchException>(() =>
            fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.ToArray()[2], success: false)));

        var state = await fixture.State(id);
        Assert.Equal(SagaStatus.Failed, state.Status);
        Assert.Equal(SagaCompensationStatus.Failed, state.Compensations[0].Status);
        Assert.Equal(SagaCompensationStatus.Planned, state.Compensations[1].Status);
        Assert.Contains("compensation transport unavailable", state.FailureReason);
        Assert.Equal(3, fixture.Bus.Commands.Count);
    }

    private static async Task SucceedForward(CoordinationTestFixture fixture, int commandIndex) =>
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.ToArray()[commandIndex]));

    private static void AssertCompensation(SagaCommand command, string name, string stepId)
    {
        Assert.Equal(name, command.Name);
        Assert.Equal(stepId, command.Headers.StepId);
        Assert.True(command.Headers.IsCompensation);
    }
}
