using System.Collections.Concurrent;
using System.Text.Json;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Definitions;
using SagaFlow.Core.Messages;
using SagaFlow.Core.State;
using SagaFlow.Engine.ContextState;
using SagaFlow.Engine.Coordination;
using SagaFlow.Engine.Messaging;
using SagaFlow.Engine.Persistence.InMemory;
using SagaFlow.Engine.ProcessDefinition;
using SagaFlow.Engine.Notifications;
using SagaFlow.Engine.Consistency;

namespace SagaFlow.Engine.Tests;

internal sealed class CoordinationTestFixture
{
    public InMemorySagaStateStore Store { get; } = new();
    public RecordingMessageBus Bus { get; } = new();
    public ProcessDefinitionRegistry Registry { get; } = new();
    public ISagaClock Clock { get; } = new TestClock();
    public ProcessStateManager States { get; } = new();
    public SagaInstanceManager Instances { get; }
    public SagaStepExecutor Executor { get; }
    public CompensationManager Compensations { get; }
    public SagaCoordinator Coordinator { get; }
    public OutcomeEventReceiver Receiver { get; }

    public CoordinationTestFixture(SagaDefinition? definition = null, SagaStateChangePublisher? changes = null, IServiceProvider? services = null)
    {
        if (definition is not null) Registry.RegisterLocalBatch([definition]);
        Instances = new(Clock);
        var dispatcher = new CommandDispatcher(Bus);
        Executor = new(Store, dispatcher, Clock, changes);
        Compensations = new(Store, dispatcher, Clock, changes);
        Coordinator = new(Registry, Store, Instances, Executor, new(), States, Compensations, Clock, changes, services);
        Receiver = new(Coordinator);
    }

    public async Task<SagaInstance> State(Guid id) => (await ((ISagaStateStore)Store).FindAsync(id))!;
    public SagaOutcomeEvent Outcome(SagaCommand command, bool success = true, object? result = null) => new()
    { Headers = command.Headers, IsSuccess = success, Result = JsonSerializer.SerializeToElement(result ?? new { available = true }) };

    public static SagaDefinition Linear(int count = 2, bool compensate = true, string name = "process")
    {
        var steps = Enumerable.Range(0, count).Select(i => new SagaStepDefinition
        {
            Name = $"step-{i}", Command = $"Command{i}", Compensation = compensate ? new() { Command = $"Undo{i}" } : null,
            Transitions = [new() { Event = "success", NextStep = i + 1 < count ? $"step-{i + 1}" : "completed" }, new() { Event = "failure", NextStep = "compensation" }]
        }).ToArray();
        return new() { Domain = "domain", Name = name, Version = "1.0", Steps = steps };
    }

    internal sealed class RecordingMessageBus : ISagaMessageBus
    {
        public ConcurrentQueue<SagaCommand> Commands { get; } = new();
        public Func<SagaCommand, CancellationToken, ValueTask>? OnPublish { get; set; }
        public async ValueTask PublishAsync(SagaCommand command, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (OnPublish is not null) await OnPublish(command, cancellationToken);
            Commands.Enqueue(command);
        }
    }

    private sealed class TestClock : ISagaClock
    {
        private long _ticks;
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch.AddTicks(Interlocked.Increment(ref _ticks));
    }
}
