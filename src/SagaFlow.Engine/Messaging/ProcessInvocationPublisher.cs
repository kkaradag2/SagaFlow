using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Messages;
using SagaFlow.Core.State;
using SagaFlow.Engine.ProcessDefinition;
using SagaFlow.Engine.Notifications;
namespace SagaFlow.Engine.Messaging;

public sealed class ProcessInvocationPublishException(string sourceInstanceId, string invocationId, Exception inner)
    : Exception("Invocation publication failed or is uncertain; explicit retry must retain its identity.", inner)
{
    public string SourceInstanceId { get; } = sourceInstanceId;
    public string InvocationId { get; } = invocationId;
}

public sealed class ProcessInvocationPublisher(ISagaStateStore store, IProcessInvocationBus bus, ProcessDefinitionRegistry definitions, ISagaClock clock, SagaStateChangePublisher? changes = null)
{
    public async Task PublishForStepAsync(string instanceId, string stepId, CancellationToken cancellationToken = default)
    {
        var instance = await store.FindAsync(instanceId, cancellationToken) ?? throw new KeyNotFoundException("Source Saga not found.");
        var request = instance.Steps.Single(s => s.StepId == stepId).Invocation;
        if (request is not null) await PublishAsync(instanceId, request.Identity.InvocationId, false, cancellationToken);
    }

    /// <summary>Requested retries are explicit. A reservation authorizes one in-flight attempt at its atomic linearization point.</summary>
    public async Task PublishAsync(string instanceId, string invocationId, bool retry = false, CancellationToken cancellationToken = default)
    {
        var attempt = Guid.NewGuid().ToString("N");
        var request = await store.UpdateAsync(instanceId, instance =>
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
            if (step.Publication?.ActiveAttemptId is not null) throw new InvalidOperationException("An invocation publish is already in flight.");
            if (progress.Status != ExternalProcessStatus.Prepared && !(retry && progress.Status == ExternalProcessStatus.Requested))
                throw new InvalidOperationException("Only Prepared or explicitly retried Requested invocations can be published.");
            step.InvocationProgress = progress with { Status = ExternalProcessStatus.Requested, UpdatedAt = clock.UtcNow };
            step.Publication = new(attempt, clock.UtcNow, null);
            return step.Invocation;
        }, cancellationToken);
        if (changes is not null) await changes.PublishAsync(instanceId);
        Exception? failure = null;
        try { await bus.PublishAsync(request, cancellationToken); }
        catch (Exception error) { failure = error; }
        // Do not roll back Requested: delivery may already have happened, and an acceptance may already have arrived.
        try
        {
            await store.UpdateAsync(instanceId, instance =>
            {
                var step = instance.Steps.Single(s => s.Invocation?.Identity.InvocationId == invocationId);
                if (step.Publication?.ActiveAttemptId != attempt) throw new InvalidOperationException("Publish claim changed unexpectedly.");
                step.Publication = new(null, step.Publication.AttemptedAt, failure?.Message);
                return true;
            }, CancellationToken.None);
        }
        catch (Exception bookkeepingFailure)
        {
            throw new ProcessInvocationPublishException(instanceId, invocationId,
                failure is null ? bookkeepingFailure : new AggregateException(failure, bookkeepingFailure));
        }
        if (changes is not null) await changes.PublishAsync(instanceId);
        if (failure is not null) throw new ProcessInvocationPublishException(instanceId, invocationId, failure);
    }

    /// <summary>Explicit future hook: only an actual terminal target state can produce this message. No automatic terminal hook.</summary>
    public async Task PublishTerminalAsync(string targetInstanceId, CancellationToken cancellationToken = default)
    {
        var outcome = await store.UpdateAsync(targetInstanceId, instance =>
        {
            var incoming = instance.IncomingInvocation ?? throw new InvalidOperationException("Target has no incoming invocation.");
            var status = instance.Status switch
            {
                SagaStatus.Completed => ExternalProcessStatus.Completed,
                SagaStatus.Failed => ExternalProcessStatus.Failed,
                SagaStatus.Compensated => ExternalProcessStatus.Compensated,
                _ => throw new InvalidOperationException("Target Saga is not terminal.")
            };
            var target = incoming.Identity.Target;
            if (new ProcessIdentity(instance.DefinitionDomain, instance.DefinitionName, instance.DefinitionVersion) != target
                || definitions.Find(new(target.Domain, target.Process, target.Version))?.Origin != DefinitionOrigin.Local)
                throw new InvalidOperationException("Target process is not locally owned.");
            if (instance.InvocationTerminalOutcome is { } existing && existing.Status != status)
                throw new InvalidOperationException("Conflicting terminal outcome.");
            return instance.InvocationTerminalOutcome ??= new(new(incoming.Identity, Guid.NewGuid().ToString("N"), clock.UtcNow), instance.Id, status);
        }, cancellationToken);
        if (changes is not null) await changes.PublishAsync(targetInstanceId);
        await bus.PublishAsync(outcome, cancellationToken);
    }
}
