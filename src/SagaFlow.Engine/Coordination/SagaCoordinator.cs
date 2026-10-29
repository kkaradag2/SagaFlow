using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Messages;
using SagaFlow.Core.State;
using SagaFlow.Engine.ContextState;
using SagaFlow.Engine.ProcessDefinition;
using SagaFlow.Engine.Notifications;
using SagaFlow.Engine.Consistency;

namespace SagaFlow.Engine.Coordination;

public sealed class SagaCoordinator(ProcessDefinitionRegistry definitions, ISagaStateStore store, SagaInstanceManager instances,
    SagaStepExecutor executor, TransitionResolver resolver, ProcessStateManager states, CompensationManager compensations, ISagaClock clock,
    SagaStateChangePublisher? changes = null, IServiceProvider? services = null) : ISagaFlow
{
    public string Name => nameof(SagaCoordinator);

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

    public async Task HandleOutcomeAsync(SagaOutcomeEvent outcome, CancellationToken cancellationToken = default)
    {
        if (outcome is null) throw new InvalidSagaOutcomeException("Outcome is required.");
        if (outcome.Headers is null) throw new InvalidSagaOutcomeException("Outcome headers are required.");
        if (string.IsNullOrWhiteSpace(outcome.Headers.SagaInstanceId))
            throw new InvalidSagaOutcomeException("Outcome Saga instance ID is required.");
        if (outcome.Result.ValueKind == System.Text.Json.JsonValueKind.Undefined)
            throw new InvalidSagaOutcomeException("Outcome result must be valid JSON.");

        (SagaCommand? Command, string? Error) decision;
        try
        {
            decision = await store.UpdateAsync(outcome.Headers.SagaInstanceId, instance =>
            {
                if (outcome.Headers.CorrelationId != instance.CorrelationId)
                    throw new InvalidSagaOutcomeException("Outcome correlation does not match the Saga instance.");
                if (outcome.Headers.IsCompensation)
                    return (Command: compensations.ApplyOutcome(instance, outcome), Error: (string?)null);
                var step = instance.Steps.SingleOrDefault(s => s.StepId == outcome.Headers.StepId)
                    ?? throw new InvalidSagaOutcomeException("Outcome refers to an unknown Saga step.");
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
                var target = resolver.Resolve(registered.Definition.Steps.Single(s => s.Name == step.StepId), outcome.IsSuccess, outcome.Result);
                states.ApplyOutcome(instance, outcome, target, clock.UtcNow, registered.Definition);
                if (target == "completed") return (Command: (SagaCommand?)null, Error: (string?)null);
                if (target == "compensation") return (Command: outcome.IsSuccess ? null : compensations.Begin(instance), Error: (string?)null);
                var next = instance.Steps.Single(s => s.StepId == target);
                if (next.Status != SagaStepStatus.Pending)
                {
                    var error = $"Runtime cycle/revisit rejected: step '{target}' has already been dispatched in Saga '{instance.Id}'.";
                    instance.Status = SagaStatus.Failed;
                    instance.CurrentStep = null;
                    instance.FailureReason = error;
                    return (Command: (SagaCommand?)null, Error: error);
                }
                return (Command: (SagaCommand?)executor.Prepare(instance), Error: (string?)null);
            }, cancellationToken);
            if (changes is not null) await changes.PublishAsync(outcome.Headers.SagaInstanceId);
        }
        catch (KeyNotFoundException error)
        {
            throw new InvalidSagaOutcomeException("Outcome refers to an unknown Saga instance.", error);
        }

        if (decision.Error is not null) throw new InvalidOperationException(decision.Error);
        if (decision.Command is { Headers.IsCompensation: true } compensation)
            await compensations.PublishPreparedAsync(compensation, cancellationToken);
        else if (decision.Command is not null)
            await executor.PublishPreparedAsync(decision.Command, cancellationToken);
        else
            await new DomainRollbackManager(store, services ?? EmptyServiceProvider.Instance, clock, changes)
                .ExecuteIfPlannedAsync(outcome.Headers.SagaInstanceId, cancellationToken);
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();
        public object? GetService(Type serviceType) => null;
    }
}
