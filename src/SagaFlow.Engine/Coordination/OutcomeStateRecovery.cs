using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Messages;
using SagaFlow.Core.State;
using SagaFlow.Engine.Notifications;
using SagaFlow.Engine.ProcessDefinition;
using SagaFlow.Engine.Messaging;

namespace SagaFlow.Engine.Coordination;

/// <summary>Preserves dispatch evidence for reconciliation when authoritative Saga state is missing.</summary>
internal sealed class OutcomeStateRecovery(ProcessDefinitionRegistry definitions, ISagaStateStore store,
    ISagaClock clock, SagaStateChangePublisher? changes)
{
    private const string IncompleteHistory = "The participant outcome was recovered, but the Saga context and execution history are unavailable. Execution requires reconciliation.";
    private const string UnverifiedFreshness = "The Saga instance is missing. A dispatch snapshot cannot prove that a later command was not already published. Reconcile against authoritative Saga history before replaying this outcome.";

    /// <summary>Quarantines unverified snapshots and records legacy evidence without creating executable state.</summary>
    internal async Task PreserveIfMissingAsync(SagaOutcomeEvent outcome, CancellationToken cancellationToken)
    {
        var headers = outcome.Headers;
        if (await store.FindAsync(headers.SagaInstanceId, cancellationToken) is not null) return;
        ValidateWireEvidence(outcome);
        ValidateRecoveryMetadata(headers);

        var registered = definitions.Find(new(headers.ProcessDomain, headers.ProcessName, headers.ProcessVersion));
        if (registered?.Origin != DefinitionOrigin.Local)
            throw new SagaOutcomeRecoveryRequiredException("Recovery requires the exact locally owned process definition and version from the outcome.");
        var definition = registered.Definition;
        var step = definition.Steps.SingleOrDefault(candidate => candidate.Name == headers.StepId);
        var command = headers.IsCompensation ? step?.Compensation?.Command : step is { External: false } ? step.Command : null;
        if (command != headers.CommandName)
            throw new SagaOutcomeRecoveryRequiredException("Outcome step, execution kind or command conflicts with the loaded definition. Recovery requires a compatible definition.");

        // A valid snapshot describes the captured dispatch. It cannot establish the latest dispatch.
        if (SagaExecutionSnapshotCodec.Restore(outcome.RecoverySnapshot, headers, definition) is not null)
            throw new SagaOutcomeRecoveryRequiredException(UnverifiedFreshness);

        var snapshot = new SagaInstance
        {
            Id = headers.SagaInstanceId, CorrelationId = headers.CorrelationId,
            DefinitionDomain = definition.Domain, DefinitionName = definition.Name, DefinitionVersion = definition.Version,
            Status = SagaStatus.RecoveryRequired, UpdatedAt = clock.UtcNow, FailureReason = IncompleteHistory,
            Steps = SagaInstanceManager.CreateStepStates(definition, SagaStepStatus.Unknown), RecoveryOutcome = outcome
        };
        if (!headers.IsCompensation)
            snapshot.Steps.Single(candidate => candidate.StepId == headers.StepId).Execution = headers;

        try
        {
            // Revision zero is insert-only. A competing insert cannot replace any existing history.
            await store.SaveAsync(snapshot, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            if (await store.FindAsync(headers.SagaInstanceId, cancellationToken) is null) throw;
            return;
        }
        if (changes is not null) await changes.PublishAsync(snapshot.Id);
        throw new SagaOutcomeRecoveryRequiredException(IncompleteHistory);
    }

    /// <summary>Preserves later deliveries for a recovered snapshot without applying transitions or changing its evidence.</summary>
    internal static void PreservePendingReconciliation(SagaInstance instance, SagaOutcomeEvent outcome)
    {
        // A wire-valid conflict can be preserved even when its metadata cannot seed a snapshot.
        ValidateWireEvidence(outcome);
        var headers = outcome.Headers;
        var previous = instance.RecoveryOutcome;
        if (headers.CorrelationId != instance.CorrelationId || headers.ProcessDomain != instance.DefinitionDomain ||
            headers.ProcessName != instance.DefinitionName || headers.ProcessVersion != instance.DefinitionVersion)
            throw new SagaOutcomeRecoveryRequiredException("Outcome identity conflicts with the recovered Saga evidence.")
                { IsConflictingEvidence = true };
        if (previous is not null && (previous.MessageId == outcome.MessageId ||
            previous.Headers.ExecutionId == headers.ExecutionId || previous.Headers.CommandId == headers.CommandId ||
            (previous.Headers.StepId == headers.StepId && previous.Headers.IsCompensation == headers.IsCompensation)) &&
            (previous.Headers != headers || previous.Name != outcome.Name || previous.IsSuccess != outcome.IsSuccess ||
            previous.Result.GetRawText() != outcome.Result.GetRawText()))
            throw new SagaOutcomeRecoveryRequiredException("Outcome conflicts with the execution or result already recovered for this Saga.")
                { IsConflictingEvidence = true };

        ValidateRecoveryMetadata(headers);
        throw new SagaOutcomeRecoveryRequiredException(IncompleteHistory);
    }

    private static void ValidateWireEvidence(SagaOutcomeEvent outcome)
    {
        var headers = outcome.Headers;
        if (headers.ProtocolVersion != 1 || !Guid.TryParseExact(headers.SagaInstanceId, "N", out var id) ||
            id == Guid.Empty || headers.SagaInstanceId != id.ToString("N") || string.IsNullOrWhiteSpace(headers.CorrelationId) ||
            string.IsNullOrWhiteSpace(headers.ProcessDomain) || string.IsNullOrWhiteSpace(headers.ProcessName) ||
            string.IsNullOrWhiteSpace(headers.ProcessVersion) ||
            string.IsNullOrWhiteSpace(headers.StepId) || string.IsNullOrWhiteSpace(headers.CommandName) ||
            string.IsNullOrWhiteSpace(headers.ExecutionId) || string.IsNullOrWhiteSpace(headers.CommandId) ||
            string.IsNullOrWhiteSpace(outcome.MessageId) || string.IsNullOrWhiteSpace(outcome.Name) ||
            outcome.CausationId != headers.CommandId || headers.CreatedAt == default || headers.CreatedAt.Offset != TimeSpan.Zero ||
            headers.IsCompensation && string.IsNullOrWhiteSpace(headers.ForwardExecutionId))
            throw new InvalidSagaOutcomeException("Outcome has invalid execution metadata for Saga recovery.");
    }

    private static void ValidateRecoveryMetadata(SagaMessageHeaders headers)
    {
        if (!ProcessDefinitionValidator.IsIdentifier(headers.ProcessDomain) || !ProcessDefinitionValidator.IsIdentifier(headers.ProcessName) ||
            !ProcessDefinitionValidator.IsVersion(headers.ProcessVersion) ||
            (headers.IsCompensation ? headers.ForwardExecutionId == headers.ExecutionId : headers.ForwardExecutionId is not null))
            throw new InvalidSagaOutcomeException("Outcome has invalid execution metadata for Saga recovery.");
    }
}
