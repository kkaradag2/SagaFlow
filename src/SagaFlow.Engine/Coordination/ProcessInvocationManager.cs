using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Messages;
using SagaFlow.Core.State;
using SagaFlow.Engine.ProcessDefinition;
using SagaFlow.Engine.Notifications;

namespace SagaFlow.Engine.Coordination;

/// <summary>Local state operations only. Calls do not send requests or advance local Saga execution.</summary>
public sealed class ProcessInvocationManager(ISagaStateStore store, ProcessDefinitionRegistry definitions, ISagaClock clock, SagaStateChangePublisher? changes = null)
{
    internal async ValueTask<ProcessInvocationReceipt> PrepareAsync<T>(string instanceId, string stepId, string target, T payload, CancellationToken cancellationToken)
    {
        var receipt = await store.UpdateAsync(instanceId, instance =>
        {
            if (instance.CurrentStep != stepId) throw new InvalidOperationException("Execution context is stale.");
            var request = new ProcessInvocationResolver(definitions, clock).Create(instance, target, payload);
            var step = instance.Steps.Single(s => s.StepId == stepId);
            if (step.InvocationProgress is null)
            {
                var now = clock.UtcNow;
                step.InvocationProgress = new(ExternalProcessStatus.Prepared, null, now, now, null);
                instance.UpdatedAt = now;
            }
            // The receipt describes registration, even if later observations have advanced the relationship.
            return new ProcessInvocationReceipt(request.Identity.InvocationId, request.Identity.Target, ExternalProcessStatus.Prepared);
        }, cancellationToken);
        if (changes is not null) await changes.PublishAsync(instanceId);
        return receipt;
    }

    public async ValueTask<CrossDomainInstanceRelationship> FindAsync(string sourceInstanceId, string invocationId, CancellationToken cancellationToken = default)
    {
        var instance = await store.FindAsync(sourceInstanceId, cancellationToken) ?? throw new KeyNotFoundException("Source instance not found.");
        var step = FindStep(instance, invocationId);
        return Snapshot(step);
    }

    /// <summary>Explicit observation/application call for future messaging adapters. Identity must match the stored request exactly.</summary>
    public async ValueTask<CrossDomainInstanceRelationship> ObserveAsync(ProcessInvocationIdentity identity, ExternalProcessStatus status,
        string? targetSagaInstanceId = null, CancellationToken cancellationToken = default,
        InvocationMessage? message = null, string? fingerprint = null)
    {
        var relationship = await store.UpdateAsync(identity.SourceSagaInstanceId, instance =>
        {
            EnsureOwner(instance, identity.Source);
            var step = FindStep(instance, identity.InvocationId);
            if (step.Invocation!.Identity != identity) throw new InvalidOperationException("Invocation source/target/correlation mismatch.");
            if (message is not null)
            {
                message.ValidateAgainst(identity);
                if (string.IsNullOrWhiteSpace(fingerprint)) throw new ArgumentException("Outcome fingerprint is required.");
                var priorMessage = step.ObservedMessages.SingleOrDefault(m => m.MessageId == message.MessageId);
                if (priorMessage is not null)
                {
                    if (priorMessage.Fingerprint != fingerprint) throw new InvalidOperationException("Same MessageId was reused for conflicting outcome content.");
                    return Snapshot(step);
                }
            }
            var progress = step.InvocationProgress ?? throw new InvalidOperationException("Invocation has not been registered.");
            if (!Enum.IsDefined(status)) throw new ArgumentException("Unknown external status.", nameof(status));
            if (targetSagaInstanceId is not null && string.IsNullOrWhiteSpace(targetSagaInstanceId)) throw new ArgumentException("Invalid target Saga ID.");
            if (progress.TargetSagaInstanceId is not null && targetSagaInstanceId is not null && progress.TargetSagaInstanceId != targetSagaInstanceId)
                throw new InvalidOperationException("Target Saga identity cannot change.");
            var targetId = progress.TargetSagaInstanceId ?? targetSagaInstanceId;
            var lateAcceptance = status == ExternalProcessStatus.Accepted && targetSagaInstanceId == progress.TargetSagaInstanceId
                && progress.Status is ExternalProcessStatus.Running or ExternalProcessStatus.Completed or ExternalProcessStatus.Failed or ExternalProcessStatus.Compensated;
            if (lateAcceptance)
            {
                if (message is not null) step.ObservedMessages = Array.AsReadOnly(step.ObservedMessages.Append(new InvocationObservedMessage(message.MessageId, fingerprint!)).ToArray());
                return Snapshot(step);
            }
            if (status is ExternalProcessStatus.Accepted or ExternalProcessStatus.Running or ExternalProcessStatus.Completed or ExternalProcessStatus.Failed or ExternalProcessStatus.Compensated)
            {
                if (targetId is null) throw new InvalidOperationException("Accepted/running/terminal observations require a target Saga ID.");
            }
            else if (targetId is not null) throw new InvalidOperationException("Target Saga ID cannot be bound before acceptance.");
            if (status != progress.Status && !CanTransition(progress.Status, status))
                throw new InvalidOperationException($"Invalid external transition {progress.Status} -> {status}.");
            var now = clock.UtcNow;
            if (now < progress.UpdatedAt || (progress.LastObservedAt is { } observed && now < observed))
                throw new InvalidOperationException("Observation clock moved backwards.");
            step.InvocationProgress = new(status, targetId, progress.CreatedAt,
                status == progress.Status ? progress.UpdatedAt : now, now);
            if (message is not null) step.ObservedMessages = Array.AsReadOnly(step.ObservedMessages.Append(new InvocationObservedMessage(message.MessageId, fingerprint!)).ToArray());
            instance.UpdatedAt = now;
            return Snapshot(step);
        }, cancellationToken);
        if (changes is not null) await changes.PublishAsync(identity.SourceSagaInstanceId);
        return relationship;
    }

    /// <summary>Create only local target state, without dispatching steps. Atomic store claim prevents duplicate creation.</summary>
    public async ValueTask<SagaInstance> GetOrCreateTargetAsync(ProcessInvocationRequest request, CancellationToken cancellationToken = default)
    {
        var target = request.Identity.Target;
        var definition = definitions.Find(new(target.Domain, target.Process, target.Version));
        if (definition?.Origin != DefinitionOrigin.Local) throw new InvalidOperationException("Target definition is not locally owned.");
        var instance = await store.GetOrCreateIncomingAsync(request,
            () => new SagaInstanceManager(clock).Create(definition.Definition, request.Payload), clock.UtcNow, cancellationToken);
        if (changes is not null) await changes.PublishAsync(instance.Id);
        return instance;
    }

    public async ValueTask<IncomingInvocation> AttachIncomingAsync(string targetInstanceId, ProcessInvocationRequest request,
        CancellationToken cancellationToken = default)
    {
        var instance = await store.FindAsync(targetInstanceId, cancellationToken) ?? throw new KeyNotFoundException("Target instance not found.");
        EnsureOwner(instance, request.Identity.Target);
        var bound = await store.AttachIncomingAsync(targetInstanceId, request, clock.UtcNow, cancellationToken);
        if (changes is not null) await changes.PublishAsync(bound.Id);
        return bound.IncomingInvocation!;
    }

    /// <summary>Compatibility attachment for a locally started Saga: its business context is the incoming payload.
    /// Source correlation is retained only in provenance; the target keeps its own local correlation.</summary>
    public async ValueTask<IncomingInvocation> AttachIncomingAsync(string targetInstanceId, ProcessInvocationIdentity identity,
        DateTimeOffset sourceCreatedAt, CancellationToken cancellationToken = default)
    {
        var instance = await store.FindAsync(targetInstanceId, cancellationToken) ?? throw new KeyNotFoundException("Target instance not found.");
        var request = new ProcessInvocationRequest(identity, instance.IncomingInvocation?.Payload ?? instance.Context,
            new(identity, Guid.NewGuid().ToString("N"), sourceCreatedAt));
        return await AttachIncomingAsync(targetInstanceId, request, cancellationToken);
    }

    /// <summary>Eligibility snapshot only, not a reservation or delivery guarantee. Requested retransmission requires S2-3 policy.</summary>
    public async ValueTask<bool> CanPublishAsync(string sourceInstanceId, string invocationId, CancellationToken cancellationToken = default)
    {
        var instance = await store.FindAsync(sourceInstanceId, cancellationToken) ?? throw new KeyNotFoundException("Source instance not found.");
        var step = FindStep(instance, invocationId);
        EnsureOwner(instance, step.Invocation!.Identity.Source);
        return step.Invocation.Identity.SourceSagaInstanceId == instance.Id
            && step.Invocation.Identity.SourceStep == step.StepId && step.Invocation.Identity.CorrelationId == instance.CorrelationId
            && instance.Status == SagaStatus.Running && instance.CurrentStep == step.StepId
            && step.Status == SagaStepStatus.Dispatched && step.InvocationProgress?.Status == ExternalProcessStatus.Prepared;
    }

    private void EnsureOwner(SagaInstance instance, ProcessIdentity identity)
    {
        if (new ProcessIdentity(instance.DefinitionDomain, instance.DefinitionName, instance.DefinitionVersion) != identity
            || definitions.Find(new(identity.Domain, identity.Process, identity.Version))?.Origin != DefinitionOrigin.Local)
            throw new InvalidOperationException("Instance process identity/local ownership mismatch.");
    }
    private static SagaStepState FindStep(SagaInstance instance, string invocationId) =>
        instance.Steps.SingleOrDefault(s => s.Invocation?.Identity.InvocationId == invocationId)
        ?? throw new KeyNotFoundException("Invocation not found in source instance.");
    private static CrossDomainInstanceRelationship Snapshot(SagaStepState step)
    {
        var progress = step.InvocationProgress ?? throw new InvalidOperationException("Invocation progress is unavailable.");
        return new(step.Invocation!.Identity, progress.TargetSagaInstanceId, progress.Status, progress.UpdatedAt, progress.CreatedAt, progress.LastObservedAt, step.Reconciliation);
    }
    private static bool CanTransition(ExternalProcessStatus from, ExternalProcessStatus to) => (from, to) switch
    {
        (ExternalProcessStatus.Prepared, ExternalProcessStatus.Requested) => true,
        (ExternalProcessStatus.Requested, ExternalProcessStatus.Accepted or ExternalProcessStatus.Rejected) => true,
        (ExternalProcessStatus.Accepted, ExternalProcessStatus.Running or ExternalProcessStatus.Completed or ExternalProcessStatus.Failed or ExternalProcessStatus.Compensated) => true,
        (ExternalProcessStatus.Running, ExternalProcessStatus.Completed or ExternalProcessStatus.Failed or ExternalProcessStatus.Compensated) => true,
        _ => false
    };
}
