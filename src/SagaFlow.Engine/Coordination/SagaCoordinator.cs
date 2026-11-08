using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Messages;
using System.Text.Json;
using SagaFlow.Engine.Messaging;
using SagaFlow.Core.State;
using SagaFlow.Engine.ContextState;
using SagaFlow.Engine.ProcessDefinition;
using SagaFlow.Engine.Notifications;
using SagaFlow.Engine.Consistency;

namespace SagaFlow.Engine.Coordination;

public sealed class SagaCoordinator(ProcessDefinitionRegistry definitions, ISagaStateStore store, SagaInstanceManager instances,
    SagaStepExecutor executor, TransitionResolver resolver, ProcessStateManager states, CompensationManager compensations, ISagaClock clock,
    SagaStateChangePublisher? changes = null, IServiceProvider? services = null, InvocationExecutionContext? invocationContext = null, ProcessInvocationPublisher? invocationPublisher = null) : ISagaFlow
{
    public string Name => nameof(SagaCoordinator);

    public async Task<ProcessInvocationReceipt> InvokeAsync<TPayload>(string target, TPayload payload, CancellationToken cancellationToken = default)
    {
        if (invocationContext?.InstanceId is not { } id || invocationContext.StepId is not { } step)
            throw new InvalidOperationException("InvokeAsync requires an active Domain Starter local step handler scope.");
        return await new ProcessInvocationManager(store, definitions, clock, changes)
            .PrepareAsync(id, step, target, payload, cancellationToken);
    }

    public async Task<Guid> StartAsync<TPayload>(TPayload payload, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var local = definitions.Snapshot().Where(d => d.Origin == DefinitionOrigin.Local).ToArray();
        if (local.Length == 0) throw new InvalidOperationException("No local Saga process definition is loaded.");
        if (local.Length != 1) throw new InvalidOperationException("Saga process selection is ambiguous: exactly one local definition/version is required.");
        executor.EnsureTransport();
        var instance = instances.Create(local[0].Definition, payload);
        await store.SaveAsync(instance, cancellationToken);
        if (changes is not null) await changes.PublishAsync(instance.Id);
        try { await executor.ExecuteAsync(instance.Id, cancellationToken); }
        catch (SagaFlow.Engine.Messaging.ProcessInvocationPublishException) { throw; }
        catch (SagaDispatchException) { throw; }
        catch (Exception error)
        {
            // Cancellation between creation and the first dispatch claim must not orphan a Running instance.
            await store.UpdateAsync(instance.Id, state =>
            {
                if (state.Status == SagaStatus.Running && state.Steps.All(s => s.Status == SagaStepStatus.Pending))
                {
                    state.Status = SagaStatus.Failed;
                    state.CurrentStep = null;
                    state.FailureReason = $"Initial command could not be prepared: {error.Message}";
                    state.UpdatedAt = clock.UtcNow;
                }
                return true;
            }, CancellationToken.None);
            if (changes is not null) await changes.PublishAsync(instance.Id);
            throw new SagaDispatchException(instance.Id, instance.CurrentStep!, error);
        }
        return Guid.Parse(instance.Id);
    }

    /// <summary>Atomically claims the first target dispatch. Existing dispatch/failure/terminal state is never restarted.</summary>
    public async Task StartIncomingAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        executor.EnsureTransport();
        var command = await store.UpdateAsync(instanceId, instance =>
        {
            var registered = definitions.Find(new(instance.DefinitionDomain, instance.DefinitionName, instance.DefinitionVersion));
            if (instance.IncomingInvocation is null || registered?.Origin != DefinitionOrigin.Local)
                throw new InvalidOperationException("Incoming target must be owned by a loaded local definition.");
            if (instance.Status != SagaStatus.Running || instance.Steps.Any(step => step.Status != SagaStepStatus.Pending)) return (SagaCommand?)null;
            if (instance.CurrentStep != registered.Definition.Steps[0].Name) throw new InvalidOperationException("Incoming target has an invalid initial step.");
            return executor.Prepare(instance);
        }, cancellationToken);
        if (command is not null && changes is not null) await changes.PublishAsync(instanceId);
        try { if (command is not null) await executor.PublishPreparedAsync(command, cancellationToken); }
        catch { await NotifyTerminalAsync(instanceId, CancellationToken.None); throw; }
        await NotifyTerminalAsync(instanceId, cancellationToken);
    }

    public Task HandleExternalTerminalAsync(ProcessInvocationTerminalOutcome outcome, CancellationToken cancellationToken = default) =>
        HandleOutcomeCoreAsync(new SagaOutcomeEvent
        {
            MessageId = outcome.Message.MessageId,
            Headers = new() { SagaInstanceId = outcome.Message.Identity.SourceSagaInstanceId, StepId = outcome.Message.Identity.SourceStep,
                CorrelationId = outcome.Message.Identity.CorrelationId },
            IsSuccess = outcome.Status == ExternalProcessStatus.Completed,

        }, cancellationToken, outcome.Message.Identity, outcome.Status, outcome.TargetSagaInstanceId);

    public Task HandleExternalRejectedAsync(ProcessInvocationRejected rejected, CancellationToken cancellationToken = default) =>
        HandleOutcomeCoreAsync(new SagaOutcomeEvent
        {
            MessageId = rejected.Message.MessageId,
            Headers = new() { SagaInstanceId = rejected.Message.Identity.SourceSagaInstanceId, StepId = rejected.Message.Identity.SourceStep,
                CorrelationId = rejected.Message.Identity.CorrelationId }, IsSuccess = false,

        }, cancellationToken, rejected.Message.Identity, ExternalProcessStatus.Rejected);

    public Task HandleOutcomeAsync(SagaOutcomeEvent outcome, CancellationToken cancellationToken = default) =>
        HandleOutcomeCoreAsync(outcome, cancellationToken);

    private async Task HandleOutcomeCoreAsync(SagaOutcomeEvent outcome, CancellationToken cancellationToken,
        ProcessInvocationIdentity? externalIdentity = null, ExternalProcessStatus? externalStatus = null, string? externalTargetSagaId = null)
    {
        if (outcome is null) throw new InvalidSagaOutcomeException("Outcome is required.");
        if (outcome.Headers is null) throw new InvalidSagaOutcomeException("Outcome headers are required.");
        if (string.IsNullOrWhiteSpace(outcome.Headers.SagaInstanceId))
            throw new InvalidSagaOutcomeException("Outcome Saga instance ID is required.");
        if (outcome.Result.ValueKind == System.Text.Json.JsonValueKind.Undefined)
            throw new InvalidSagaOutcomeException("Outcome result must be valid JSON.");

        (SagaCommand? Command, string? Error, bool Changed) decision;
        try
        {
            decision = await store.UpdateAsync(outcome.Headers.SagaInstanceId, instance =>
            {
                if (outcome.Headers.CorrelationId != instance.CorrelationId)
                    throw new InvalidSagaOutcomeException("Outcome correlation does not match the Saga instance.");
                if (outcome.Headers.IsCompensation)
                    return (Command: compensations.ApplyOutcome(instance, outcome), Error: (string?)null, Changed: true);
                var step = instance.Steps.SingleOrDefault(s => s.StepId == outcome.Headers.StepId)
                    ?? throw new InvalidSagaOutcomeException("Outcome refers to an unknown Saga step.");
                if (externalIdentity is not null)
                {
                    if (step.Invocation?.Identity != externalIdentity || step.InvocationProgress?.Status != externalStatus || step.InvocationProgress?.TargetSagaInstanceId != externalTargetSagaId)
                        throw new InvalidSagaOutcomeException("External terminal does not match the observed invocation.");
                    if (!step.External || instance.Status != SagaStatus.Running || instance.CurrentStep != step.StepId
                        || step.Status != SagaStepStatus.Dispatched || step.Reconciliation is not null )
                        return (Command: (SagaCommand?)null, Error: (string?)null, Changed: false);
                }
                else if (step.External) throw new InvalidSagaOutcomeException("Participant outcomes cannot complete external process steps.");
                if (externalIdentity is null) ExecutionMetadata.Validate(step.Execution, outcome);
                if (instance.Status == SagaStatus.TimedOut && instance.CurrentStep == step.StepId && step.Status == SagaStepStatus.TimedOut)
                    throw new LateSagaOutcomeException("Outcome arrived after the active Saga step timed out; it must remain available for explicit resolution.");
                if (step.Result.HasValue && (step.Status is SagaStepStatus.Completed or SagaStepStatus.Failed))
                    throw new DuplicateSagaOutcomeException("Outcome was already processed; duplicate outcomes cannot advance the Saga again.");
                if (instance.Status == SagaStatus.Failed && instance.CurrentStep is null && step.Status == SagaStepStatus.Failed)
                    throw new InvalidOperationException("Outcome cannot be applied because the Saga terminated after command publication failed.");
                if (instance.Status != SagaStatus.Running || instance.CurrentStep != step.StepId || step.Status != SagaStepStatus.Dispatched)
                    throw new InvalidSagaOutcomeException("Outcome does not refer to the active dispatched Saga step.");
                var registered = definitions.Find(new(instance.DefinitionDomain, instance.DefinitionName, instance.DefinitionVersion));
                if (registered is null || registered.Origin != DefinitionOrigin.Local) throw new InvalidOperationException("The instance's owning local definition/version is unavailable.");
                // Record and evaluate on the same atomic working snapshot; rejected outcomes still roll back the whole update.
                var now = clock.UtcNow;
                string target;
                try
                {
                    // Older/bypassed registrations must obey the same Result=null contract as newly loaded YAML.
                    if (externalIdentity is not null) new ProcessDefinitionValidator().Validate(registered.Definition);
                    if (externalIdentity is null) states.RecordOutcome(instance, outcome, now);
                    else states.RecordExternalCompletion(instance, step, outcome.IsSuccess, now);
                    target = resolver.Resolve(registered.Definition.Steps.Single(s => s.Name == step.StepId), outcome.IsSuccess, outcome.Result, instance);
                }
                catch (Exception error) when (externalIdentity is not null && error is DefinitionValidationException or InvalidSagaOutcomeException)
                {
                    // Observation was committed separately. Commit a permanent local failure instead of requeueing it.
                    var reason = $"External step '{step.StepId}' cannot resolve its source transition: {error.Message} Manual reconciliation required.";
                    step.Status = SagaStepStatus.Failed;
                    step.Result = null;
                    step.CompletedAt = now;
                    step.Reconciliation = new(reason, now);
                    instance.Status = SagaStatus.Failed;
                    instance.CurrentStep = null;
                    instance.FailureReason = reason;
                    instance.UpdatedAt = now;
                    return (Command: (SagaCommand?)null, Error: (string?)null, Changed: true);
                }
                states.ApplyTransition(instance, target, now, registered.Definition);
                if (target == "completed") return (Command: (SagaCommand?)null, Error: (string?)null, Changed: true);
                if (target == "compensation") return (Command: compensations.Begin(instance, outcome.MessageId), Error: (string?)null, Changed: true);
                var next = instance.Steps.Single(s => s.StepId == target);
                if (next.Status != SagaStepStatus.Pending)
                {
                    var error = $"Runtime cycle/revisit rejected: step '{target}' has already been dispatched in Saga '{instance.Id}'.";
                    instance.Status = SagaStatus.Failed;
                    instance.CurrentStep = null;
                    instance.FailureReason = error;
                    return (Command: (SagaCommand?)null, Error: error, Changed: true);
                }
                return (Command: (SagaCommand?)executor.Prepare(instance, outcome.MessageId), Error: (string?)null, Changed: true);
            }, cancellationToken);
            if (decision.Changed && changes is not null) await changes.PublishAsync(outcome.Headers.SagaInstanceId);
        }
        catch (DuplicateSagaOutcomeException)
        {
            // A prior terminal notification may have failed after the local outcome committed.
            await NotifyTerminalAsync(outcome.Headers.SagaInstanceId, cancellationToken);
            throw;
        }
        catch (KeyNotFoundException error)
        {
            throw new InvalidSagaOutcomeException("Outcome refers to an unknown Saga instance.", error);
        }

        if (!decision.Changed) { await NotifyTerminalAsync(outcome.Headers.SagaInstanceId, cancellationToken); return; }
        try
        {
            if (decision.Error is not null) throw new InvalidOperationException(decision.Error);
            if (decision.Command is { Headers.IsCompensation: true } compensation)
                await compensations.PublishPreparedAsync(compensation, cancellationToken);
            else if (decision.Command is not null)
                await executor.PublishPreparedAsync(decision.Command, cancellationToken);
            else
                await new DomainRollbackManager(store, services ?? EmptyServiceProvider.Instance, clock, changes)
                    .ExecuteIfPlannedAsync(outcome.Headers.SagaInstanceId, cancellationToken);
        }
        catch
        {
            await NotifyTerminalAsync(outcome.Headers.SagaInstanceId, CancellationToken.None);
            throw;
        }
        await NotifyTerminalAsync(outcome.Headers.SagaInstanceId, cancellationToken);
    }

    private async Task NotifyTerminalAsync(string instanceId, CancellationToken cancellationToken)
    {
        if (invocationPublisher is null) return;
        var state = await store.FindAsync(instanceId, cancellationToken);
        if (state?.IncomingInvocation is not null && state.Status is SagaStatus.Completed or SagaStatus.Failed or SagaStatus.Compensated)
            await invocationPublisher.PublishTerminalAsync(instanceId, cancellationToken);
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();
        public object? GetService(Type serviceType) => null;
    }
}
