using System.Text.Json;
using SagaFlow.Core.Messages;
using SagaFlow.Core.State;
using SagaFlow.Engine.Coordination;
using SagaFlow.Core.Abstractions;
using SagaFlow.Engine.Notifications;

namespace SagaFlow.Engine.Tests;

public sealed class CompensationExecutionTests
{
    private sealed record RollbackPayload(string Name);

    private sealed class TestRollbackHandler(Func<RollbackPayload, CancellationToken, Task>? action = null) : SagaRollbackHandler<RollbackPayload>
    {
        public int Calls { get; private set; }
        public RollbackPayload? Payload { get; private set; }
        public override Task RollbackAsync(RollbackPayload payload, CancellationToken cancellationToken)
        {
            Calls++;
            Payload = payload;
            return action?.Invoke(payload, cancellationToken) ?? Task.CompletedTask;
        }
    }

    private sealed class HandlerProvider(TestRollbackHandler handler) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(SagaRollbackHandler<RollbackPayload>) ? handler : null;
    }

    private sealed class CancelBeforeRollbackClaimListener(CancellationTokenSource cancellation) : ISagaStateChangeListener
    {
        public ISagaStateStore Store { get; set; } = null!;
        public SagaInstance? PlannedState { get; private set; }

        public async ValueTask OnStateChangedAsync(SagaStateChanged notification, CancellationToken cancellationToken = default)
        {
            var state = await Store.FindAsync(notification.InstanceId, cancellationToken);
            if (state?.DomainRollback?.Status != SagaDomainRollbackStatus.Planned) return;
            PlannedState = state;
            cancellation.Cancel();
        }
    }

    [Theory]
    [InlineData("success")]
    [InlineData("cancellation")]
    [InlineData("exception")]
    [InlineData("no-handler")]
    [InlineData("no-payload-type")]
    public async Task CancellationAfterAcceptedCompensationOutcomeCannotStrandDomainRollback(string result)
    {
        using var cancellation = new CancellationTokenSource();
        var listener = new CancelBeforeRollbackClaimListener(cancellation);
        CancellationToken handlerToken = default;
        var handler = new TestRollbackHandler((_, token) =>
        {
            handlerToken = token;
            return result switch
            {
                "cancellation" => Task.FromCanceled(token),
                "exception" => Task.FromException(new InvalidOperationException("rollback rejected")),
                _ => Task.CompletedTask
            };
        });
        var fixture = new CoordinationTestFixture(CoordinationTestFixture.Linear(2),
            new SagaStateChangePublisher([listener]), result == "no-handler" ? null : new HandlerProvider(handler));
        listener.Store = fixture.Store;
        var id = await fixture.Coordinator.StartAsync(new RollbackPayload("order-42"));
        if (result == "no-payload-type")
            await fixture.Store.UpdateAsync(id.ToString("N"), state => state.PayloadType = null);
        await SucceedForward(fixture, 0);
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.Last(), success: false));
        var lastCompensationOutcome = fixture.Outcome(fixture.Bus.Commands.Last());

        await fixture.Receiver.ReceiveAsync(lastCompensationOutcome, cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.NotNull(listener.PlannedState);
        Assert.Equal(SagaStatus.Compensating, listener.PlannedState.Status);
        Assert.All(listener.PlannedState.Compensations, item => Assert.Equal(SagaCompensationStatus.Completed, item.Status));
        var state = await fixture.State(id);
        if (result is "no-handler" or "no-payload-type")
        {
            Assert.Equal(SagaStatus.Compensated, state.Status);
            Assert.Null(state.DomainRollback);
            Assert.Equal(0, handler.Calls);
        }
        else
        {
            var failed = result is "cancellation" or "exception";
            Assert.Equal(cancellation.Token, handlerToken);
            Assert.Equal(failed ? SagaStatus.Failed : SagaStatus.Compensated, state.Status);
            Assert.NotNull(state.DomainRollback);
            Assert.Equal(failed ? SagaDomainRollbackStatus.Failed : SagaDomainRollbackStatus.Completed, state.DomainRollback.Status);
            Assert.NotNull(state.DomainRollback.StartedAt);
            Assert.NotNull(state.DomainRollback.CompletedAt);
            if (failed)
            {
                Assert.False(string.IsNullOrWhiteSpace(state.DomainRollback.Error));
                Assert.Contains(state.DomainRollback.Error, state.FailureReason);
            }
            Assert.Equal(1, handler.Calls);
        }

        await Assert.ThrowsAsync<DuplicateSagaOutcomeException>(() => fixture.Receiver.ReceiveAsync(lastCompensationOutcome));
        Assert.Equal(state.Revision, (await fixture.State(id)).Revision);
        Assert.Equal(result is "no-handler" or "no-payload-type" ? 0 : 1, handler.Calls);
    }

    [Fact]
    public async Task SuccessfulSagaDoesNotCallDomainRollback()
    {
        var handler = new TestRollbackHandler();
        var fixture = new CoordinationTestFixture(CoordinationTestFixture.Linear(1), services: new HandlerProvider(handler));
        var id = await fixture.Coordinator.StartAsync(new RollbackPayload("payload"));
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.Single()));

        Assert.Equal(SagaStatus.Completed, (await fixture.State(id)).Status);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task DomainRollbackRunsAfterTheLastParticipantCompensationAndReceivesOriginalPayload()
    {
        SagaInstance? stateAtHandlerStart = null;
        ISagaStateStore store = null!;
        var handler = new TestRollbackHandler(async (_, _) => { stateAtHandlerStart = (await store.ListAsync()).Single(); });
        var fixture = new CoordinationTestFixture(CoordinationTestFixture.Linear(3), services: new HandlerProvider(handler));
        store = fixture.Store;
        var id = await fixture.Coordinator.StartAsync(new RollbackPayload("order-42"));
        await SucceedForward(fixture, 0);
        await SucceedForward(fixture, 1);
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.ToArray()[2], success: false));
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.ToArray()[3]));
        var lastCompensation = fixture.Bus.Commands.Last();
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(lastCompensation));

        var state = await fixture.State(id);
        Assert.Equal(1, handler.Calls);
        Assert.NotNull(stateAtHandlerStart);
        Assert.Equal(SagaStatus.Compensating, stateAtHandlerStart.Status);
        Assert.Equal(SagaDomainRollbackStatus.Executing, stateAtHandlerStart.DomainRollback!.Status);
        Assert.All(stateAtHandlerStart.Compensations, item => Assert.Equal(SagaCompensationStatus.Completed, item.Status));
        Assert.Equal(new RollbackPayload("order-42"), handler.Payload);
        Assert.Equal(SagaStatus.Compensated, state.Status);
        Assert.Equal(SagaDomainRollbackStatus.Completed, state.DomainRollback!.Status);
        Assert.All(state.Compensations, item => Assert.Equal(SagaCompensationStatus.Completed, item.Status));
        Assert.Equal(new[] { SagaStepStatus.Completed, SagaStepStatus.Completed, SagaStepStatus.Failed }, state.Steps.Select(step => step.Status));
    }

    [Fact]
    public async Task FailureBeforeAnySuccessfulParticipantStepRunsDomainRollbackDirectly()
    {
        var handler = new TestRollbackHandler();
        var fixture = new CoordinationTestFixture(CoordinationTestFixture.Linear(2), services: new HandlerProvider(handler));
        var id = await fixture.Coordinator.StartAsync(new RollbackPayload("payment"));
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.Single(), success: false));

        var state = await fixture.State(id);
        Assert.Empty(state.Compensations);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(SagaStatus.Compensated, state.Status);
    }

    [Fact]
    public async Task DomainRollbackWaitsBeforeCompensatedAndDuplicateOutcomeCannotRunItTwice()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new TestRollbackHandler(async (_, _) => { entered.SetResult(); await release.Task; });
        var fixture = new CoordinationTestFixture(CoordinationTestFixture.Linear(1), services: new HandlerProvider(handler));
        var id = await fixture.Coordinator.StartAsync(new RollbackPayload("one"));
        var forwardFailure = fixture.Outcome(fixture.Bus.Commands.Single(), success: false);
        var running = fixture.Receiver.ReceiveAsync(forwardFailure);
        await entered.Task;
        var during = await fixture.State(id);
        Assert.Equal(SagaStatus.Compensating, during.Status);
        Assert.Equal(SagaDomainRollbackStatus.Executing, during.DomainRollback!.Status);

        await Assert.ThrowsAsync<DuplicateSagaOutcomeException>(() => fixture.Receiver.ReceiveAsync(forwardFailure));
        release.SetResult();
        await running;
        Assert.Equal(SagaStatus.Compensated, (await fixture.State(id)).Status);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task DomainRollbackExceptionFailsSagaAndIsVisibleInState()
    {
        var handler = new TestRollbackHandler((_, _) => Task.FromException(new InvalidOperationException("order cancel failed")));
        var fixture = new CoordinationTestFixture(CoordinationTestFixture.Linear(1), services: new HandlerProvider(handler));
        var id = await fixture.Coordinator.StartAsync(new RollbackPayload("one"));
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.Single(), success: false));

        var state = await fixture.State(id);
        Assert.Equal(SagaStatus.Failed, state.Status);
        Assert.Equal(SagaDomainRollbackStatus.Failed, state.DomainRollback!.Status);
        Assert.Contains("order cancel failed", state.DomainRollback.Error);
        Assert.Contains("order cancel failed", state.FailureReason);
    }

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
        var handler = new TestRollbackHandler();
        var fixture = new CoordinationTestFixture(CoordinationTestFixture.Linear(3), services: new HandlerProvider(handler));
        var id = await fixture.Coordinator.StartAsync(new RollbackPayload("payload"));
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
        Assert.Equal(0, handler.Calls);
        Assert.Null(state.DomainRollback);
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
