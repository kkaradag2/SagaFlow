using System.Collections.Concurrent;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.State;
using SagaFlow.Engine.ContextState;
using SagaFlow.Engine.Coordination;
using SagaFlow.Engine.Messaging;
using SagaFlow.Engine.Notifications;
using SagaFlow.Engine.ProcessDefinition;
using SagaFlow.Engine.Consistency;

namespace SagaFlow.Engine.Tests;

public sealed class SagaStateChangeNotificationTests
{
    [Fact]
    public async Task NotificationsFollowSuccessfulStatePersistence()
    {
        var listener = new SnapshotListener();
        var changes = new SagaStateChangePublisher([listener]);
        var fixture = new CoordinationTestFixture(CoordinationTestFixture.Linear(), changes);
        listener.Store = fixture.Store;

        var id = await fixture.Coordinator.StartAsync(new { orderId = 1 });

        var snapshots = listener.Snapshots.ToArray();
        Assert.Equal(2, snapshots.Length);
        Assert.All(snapshots, snapshot => Assert.Equal(id.ToString("N"), snapshot.Id));
        Assert.All(snapshots[0].Steps, step => Assert.Equal(SagaStepStatus.Pending, step.Status));
        Assert.Equal(SagaStepStatus.Dispatched, snapshots[1].Steps[0].Status);
    }

    [Fact]
    public async Task ListenerFailureDoesNotBreakCoordinationOrOtherListeners()
    {
        var recording = new RecordingListener();
        var changes = new SagaStateChangePublisher([new ThrowingListener(), recording]);
        var fixture = new CoordinationTestFixture(CoordinationTestFixture.Linear(), changes);

        var id = await fixture.Coordinator.StartAsync(new { orderId = 2 });

        Assert.Equal(SagaStepStatus.Dispatched, (await fixture.State(id)).Steps[0].Status);
        Assert.Single(fixture.Bus.Commands);
        Assert.Equal(2, recording.InstanceIds.Count);
    }

    [Fact]
    public async Task CompensationTransitionsPublishPersistedSnapshotsForLiveDashboardUpdates()
    {
        var listener = new SnapshotListener();
        var changes = new SagaStateChangePublisher([listener]);
        var fixture = new CoordinationTestFixture(CoordinationTestFixture.Linear(), changes);
        listener.Store = fixture.Store;

        var id = await fixture.Coordinator.StartAsync(new { orderId = 3 });
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.ToArray()[0]));
        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.ToArray()[1], success: false));
        var dispatched = listener.Snapshots.Last();
        Assert.Equal(SagaStatus.Compensating, dispatched.Status);
        Assert.Equal(SagaCompensationStatus.Dispatched, dispatched.Compensations.Single().Status);

        await fixture.Receiver.ReceiveAsync(fixture.Outcome(fixture.Bus.Commands.Last()));

        var completed = listener.Snapshots.Last();
        Assert.Equal(id.ToString("N"), completed.Id);
        Assert.Equal(SagaStatus.Compensated, completed.Status);
        Assert.Equal(SagaCompensationStatus.Completed, completed.Compensations.Single().Status);
        Assert.Equal(7, listener.Snapshots.Count);
    }

    [Fact]
    public async Task FailedInitialSaveDoesNotPublishAFalseNotification()
    {
        var listener = new RecordingListener();
        var changes = new SagaStateChangePublisher([listener]);
        var store = new FailingSaveStore();
        var registry = new ProcessDefinitionRegistry();
        registry.RegisterLocalBatch([CoordinationTestFixture.Linear()]);
        var clock = new SystemSagaClock();
        var dispatcher = new CommandDispatcher(new CoordinationTestFixture.RecordingMessageBus());
        var executor = new SagaStepExecutor(store, dispatcher, clock, changes);
        var coordinator = new SagaCoordinator(registry, store, new SagaInstanceManager(clock), executor,
            new TransitionResolver(), new ProcessStateManager(), new CompensationManager(store, dispatcher, clock, changes), clock, changes);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StartAsync(new { orderId = 3 }));
        Assert.Empty(listener.InstanceIds);
    }

    [Fact]
    public async Task ConcurrentInstancesKeepTheirOwnNotificationIdentity()
    {
        var listener = new RecordingListener();
        var fixture = new CoordinationTestFixture(CoordinationTestFixture.Linear(),
            new SagaStateChangePublisher([listener]));

        var ids = await Task.WhenAll(
            fixture.Coordinator.StartAsync(new { orderId = 10 }),
            fixture.Coordinator.StartAsync(new { orderId = 20 }));

        Assert.Equal(2, ids.Distinct().Count());
        Assert.Equal(ids.Select(id => id.ToString("N")).Order(), listener.InstanceIds.Distinct().Order());
    }

    private sealed class RecordingListener : ISagaStateChangeListener
    {
        public ConcurrentQueue<string> InstanceIds { get; } = new();
        public ValueTask OnStateChangedAsync(SagaStateChanged notification, CancellationToken cancellationToken = default)
        {
            InstanceIds.Enqueue(notification.InstanceId);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SnapshotListener : ISagaStateChangeListener
    {
        public ISagaStateStore? Store { get; set; }
        public ConcurrentQueue<SagaInstance> Snapshots { get; } = new();
        public async ValueTask OnStateChangedAsync(SagaStateChanged notification, CancellationToken cancellationToken = default)
        {
            Snapshots.Enqueue((await Store!.FindAsync(notification.InstanceId, cancellationToken))!);
        }
    }

    private sealed class ThrowingListener : ISagaStateChangeListener
    {
        public ValueTask OnStateChangedAsync(SagaStateChanged notification, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new InvalidOperationException("dashboard unavailable"));
    }

    private sealed class FailingSaveStore : ISagaStateStore
    {
        public ValueTask SaveAsync(SagaInstance instance, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new InvalidOperationException("store unavailable"));
        public ValueTask<SagaInstance?> FindAsync(string instanceId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<SagaInstance?>(null);
        public ValueTask<IReadOnlyList<SagaInstance>> ListAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<SagaInstance>>([]);
        public ValueTask<TResult> UpdateAsync<TResult>(string instanceId, Func<SagaInstance, TResult> update,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<TResult>(new InvalidOperationException("store unavailable"));
    }
}
