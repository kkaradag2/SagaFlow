using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Messages;
using SagaFlow.Core.State;
using SagaFlow.Engine.ProcessDefinition;
using SagaFlow.Engine.Notifications;
namespace SagaFlow.Engine.Messaging;

public sealed class ProcessInvocationPublishException(string sourceInstanceId, string invocationId, Exception inner)
    : Exception("Invocation outbox processing failed; retry must retain its identity.", inner)
{
    public string SourceInstanceId { get; } = sourceInstanceId;
    public string InvocationId { get; } = invocationId;
}

public sealed class ProcessInvocationPublisher(ISagaStateStore store, IProcessInvocationBus bus, ProcessDefinitionRegistry definitions, ISagaClock clock,
    CommandDispatcher dispatcher, SagaStateChangePublisher? changes = null)
{
    public async Task PublishForStepAsync(string instanceId, string stepId, CancellationToken cancellationToken = default)
    {
        var instance = await store.FindAsync(instanceId, cancellationToken) ?? throw new KeyNotFoundException("Source Saga not found.");
        var request = instance.Steps.Single(s => s.StepId == stepId).Invocation;
        if (request is null) return;
        try { await PublishAsync(instanceId, request.Identity.InvocationId, false, cancellationToken); }
        catch (Exception error) when (error is not (InvalidOperationException or ArgumentException or OperationCanceledException))
        {
            throw new ProcessInvocationPublishException(instanceId, request.Identity.InvocationId, error);
        }
    }

    /// <summary>Atomically queues the stable request. A retry runs the dispatcher for eligible entries.</summary>
    public async Task PublishAsync(string instanceId, string invocationId, bool retry = false, CancellationToken cancellationToken = default)
    {
        await store.UpdateWithOutboxAsync(instanceId, instance =>
        {
            var step = instance.Steps.SingleOrDefault(s => s.Invocation?.Identity.InvocationId == invocationId)
                ?? throw new KeyNotFoundException("Invocation not found.");
            var identity = step.Invocation!.Identity;
            var progress = step.InvocationProgress ?? throw new InvalidOperationException("Invocation is not registered.");
            if (instance.Status != SagaStatus.Running || instance.CurrentStep != step.StepId || step.Status != SagaStepStatus.Dispatched
                || identity.SourceSagaInstanceId != instance.Id || identity.SourceStep != step.StepId || identity.CorrelationId != instance.CorrelationId
                || new ProcessIdentity(instance.DefinitionDomain, instance.DefinitionName, instance.DefinitionVersion) != identity.Source
                || definitions.Find(new(identity.Source.Domain, identity.Source.Process, identity.Source.Version))?.Origin != DefinitionOrigin.Local)
                throw new InvalidOperationException("Source Saga/step is not eligible for publication.");
            if (progress.Status != ExternalProcessStatus.Prepared && !(retry && progress.Status == ExternalProcessStatus.Requested))
                throw new InvalidOperationException("Only Prepared or explicitly retried Requested invocations can be published.");
            if (step.Publication?.Queued == true) return (false, (OutboxMessage?)null);
            step.InvocationProgress = progress with { Status = ExternalProcessStatus.Requested, UpdatedAt = clock.UtcNow };
            step.Publication = new(null, clock.UtcNow, null, Queued: true);
            return (true, bus.PrepareOutbox(OutboxMessage.FromInvocation(step.Invocation)));
        }, cancellationToken);
        if (changes is not null) await changes.PublishAsync(instanceId);
        await dispatcher.DispatchOutboxAsync(store, cancellationToken);
    }

    /// <summary>Commits an incoming Saga's terminal state, application changes, and Terminal response together.
    /// The optional synchronous mutation must perform no I/O. A repeated transition to the same status skips the mutation.</summary>
    public async Task TransitionTerminalAsync(string targetInstanceId, SagaStatus terminalStatus,
        Action<SagaInstance>? mutate = null, CancellationToken cancellationToken = default)
    {
        if (IncomingTerminalWrite.ResponseStatus(terminalStatus) is null)
            throw new ArgumentOutOfRangeException(nameof(terminalStatus));

        await store.UpdateWithOutboxAsync(targetInstanceId, instance =>
        {
            if (instance.IncomingInvocation is null) throw new InvalidOperationException("Target Saga has no incoming invocation.");
            if (IncomingTerminalWrite.ResponseStatus(instance.Status) is not null)
            {
                if (instance.Status != terminalStatus || !instance.InvocationTerminalQueued)
                    throw new InvalidOperationException("Target Saga already has a conflicting or unqueued terminal state.");
                return (false, PrepareTerminal(instance));
            }

            mutate?.Invoke(instance);
            if (instance.InvocationTerminalOutcome is not null || instance.InvocationTerminalQueued)
                throw new InvalidOperationException("Terminal response metadata is owned by SagaFlow.");
            instance.Status = terminalStatus;
            instance.CurrentStep = null;
            instance.UpdatedAt = clock.UtcNow;
            return (true, PrepareTerminal(instance));
        }, cancellationToken);
        if (changes is not null) await changes.PublishAsync(targetInstanceId);
        await dispatcher.DispatchOutboxAsync(store, cancellationToken);
    }

    internal OutboxMessage? PrepareTerminal(SagaInstance instance)
    {
        if (instance.IncomingInvocation is not { } incoming) return null;
        var status = IncomingTerminalWrite.ResponseStatus(instance.Status);
        if (status is null) return null;
        var target = incoming.Identity.Target;
        if (new ProcessIdentity(instance.DefinitionDomain, instance.DefinitionName, instance.DefinitionVersion) != target ||
            definitions.Find(new(target.Domain, target.Process, target.Version))?.Origin != DefinitionOrigin.Local)
            throw new InvalidOperationException("Target process is not locally owned.");
        if (instance.InvocationTerminalOutcome is { } existing && existing.Status != status)
            throw new InvalidOperationException("Conflicting terminal outcome.");
        if (instance.InvocationTerminalQueued) return null;
        var outcome = instance.InvocationTerminalOutcome ??= new(
            new(incoming.Identity, Guid.NewGuid().ToString("N"), clock.UtcNow), instance.Id, status.Value);
        instance.InvocationTerminalQueued = true;
        return bus.PrepareOutbox(OutboxMessage.FromInvocation(outcome));
    }
}
