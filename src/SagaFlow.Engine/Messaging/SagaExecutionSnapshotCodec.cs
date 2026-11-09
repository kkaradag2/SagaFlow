using System.Text.Json;
using SagaFlow.Core.Definitions;
using SagaFlow.Core.Messages;
using SagaFlow.Core.State;
using SagaFlow.Engine.Coordination;

namespace SagaFlow.Engine.Messaging;

/// <summary>Versioned, bounded state evidence attached to a single outgoing command.</summary>
internal static class SagaExecutionSnapshotCodec
{
    internal const int MaximumBytes = SagaCommand.MaximumRecoverySnapshotBytes;
    private sealed record Envelope(int Version, SagaInstance State);

    internal static byte[] Capture(SagaInstance state)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, state));
        if (bytes.Length > MaximumBytes)
            throw new InvalidOperationException($"Saga execution snapshot exceeds the {MaximumBytes}-byte AMQP metadata limit.");
        return bytes;
    }

    internal static SagaInstance? Restore(byte[]? bytes, SagaMessageHeaders headers, SagaDefinition definition)
    {
        if (bytes is null) return null; // Commands issued before this wire extension remain quarantinable.
        if (bytes.Length is 0 or > MaximumBytes)
            throw new SagaOutcomeRecoveryRequiredException("Execution snapshot is empty or exceeds the AMQP metadata limit.");

        Envelope? envelope;
        try { envelope = JsonSerializer.Deserialize<Envelope>(bytes); }
        catch (Exception error) when (error is JsonException or NotSupportedException or ArgumentException)
        {
            throw new SagaOutcomeRecoveryRequiredException("Execution snapshot is malformed and cannot be applied.");
        }
        if (envelope is not { Version: 1, State: not null })
            throw new SagaOutcomeRecoveryRequiredException("Execution snapshot version is unsupported.");

        try { return Validate(envelope.State, headers, definition); }
        catch (Exception error) when (error is NullReferenceException or InvalidOperationException or ArgumentException)
        {
            throw new SagaOutcomeRecoveryRequiredException("Execution snapshot has malformed or contradictory history.")
                { IsConflictingEvidence = true };
        }
    }

    private static SagaInstance Validate(SagaInstance state, SagaMessageHeaders headers, SagaDefinition definition)
    {
        if (state.Revision <= 0 || state.RecoveryOutcome is not null || state.CreatedAt == default ||
            state.UpdatedAt == default || !Enum.IsDefined(state.Status) ||
            state.Context.ValueKind == JsonValueKind.Undefined ||
            state.Id != headers.SagaInstanceId || state.CorrelationId != headers.CorrelationId ||
            state.DefinitionDomain != headers.ProcessDomain || state.DefinitionName != headers.ProcessName ||
            state.DefinitionVersion != headers.ProcessVersion || state.Steps.Count != definition.Steps.Count ||
            state.Steps.Select(step => step.StepId).Distinct(StringComparer.Ordinal).Count() != state.Steps.Count)
            throw new SagaOutcomeRecoveryRequiredException("Execution snapshot identity or structure conflicts with the outcome.")
                { IsConflictingEvidence = true };

        for (var index = 0; index < definition.Steps.Count; index++)
        {
            var actual = state.Steps[index];
            var expected = definition.Steps[index];
            if (actual.StepId != expected.Name || actual.Command != expected.Command || actual.External != expected.External ||
                !Enum.IsDefined(actual.Status) || actual.Status is SagaStepStatus.Unknown or SagaStepStatus.TimedOut ||
                actual.Status is SagaStepStatus.Completed or SagaStepStatus.Failed &&
                    (actual.Execution is null || actual.CompletedAt is null || actual.ExecutionOrder is null ||
                     !actual.External && actual.Result is null) ||
                actual.Status == SagaStepStatus.Dispatched && (actual.Execution is null || actual.ExecutionOrder is null) ||
                actual.Status == SagaStepStatus.Pending &&
                    (actual.Execution is not null || actual.ExecutionOrder is not null || actual.Result is not null) ||
                actual.Execution is { } execution &&
                    (execution.SagaInstanceId != state.Id || execution.CorrelationId != state.CorrelationId ||
                     execution.ProcessDomain != state.DefinitionDomain || execution.ProcessName != state.DefinitionName ||
                     execution.ProcessVersion != state.DefinitionVersion || execution.StepId != actual.StepId ||
                     execution.CommandName != actual.Command || execution.IsCompensation))
                throw new SagaOutcomeRecoveryRequiredException("Execution snapshot step history conflicts with the loaded definition.")
                    { IsConflictingEvidence = true };
        }

        var orders = state.Steps.Where(step => step.ExecutionOrder.HasValue)
            .Select(step => step.ExecutionOrder!.Value).Order().ToArray();
        if (!orders.SequenceEqual(Enumerable.Range(1, orders.Length)) ||
            state.Compensations.Any(item => !Enum.IsDefined(item.Status) ||
                definition.Steps.SingleOrDefault(step => step.Name == item.StepId)?.Compensation?.Command != item.Command ||
                state.Steps.Single(step => step.StepId == item.StepId).Status != SagaStepStatus.Completed ||
                item.Status is SagaCompensationStatus.Dispatched or SagaCompensationStatus.Completed && item.Execution is null))
            throw new SagaOutcomeRecoveryRequiredException("Execution snapshot order or compensation plan is contradictory.")
                { IsConflictingEvidence = true };

        if (!headers.IsCompensation)
        {
            var current = state.Steps.Single(step => step.StepId == headers.StepId);
            if (state.Status != SagaStatus.Running || state.CurrentStep != headers.StepId || current.External ||
                current.Command != headers.CommandName || current.Status != SagaStepStatus.Dispatched ||
                current.Execution != headers || state.Steps.Count(step => step.Status == SagaStepStatus.Dispatched) != 1 ||
                state.Compensations.Count != 0)
                throw new SagaOutcomeRecoveryRequiredException("Execution snapshot does not describe this dispatched command.")
                    { IsConflictingEvidence = true };
        }
        else
        {
            var current = state.Compensations.SingleOrDefault(item => item.StepId == headers.StepId);
            var defined = definition.Steps.SingleOrDefault(step => step.Name == headers.StepId);
            if (state.Status != SagaStatus.Compensating || state.CurrentStep is not null || current is null ||
                defined?.Compensation?.Command != headers.CommandName || current.Command != headers.CommandName ||
                current.Status != SagaCompensationStatus.Dispatched || current.Execution != headers ||
                state.Compensations.Count(item => item.Status == SagaCompensationStatus.Dispatched) != 1)
                throw new SagaOutcomeRecoveryRequiredException("Execution snapshot does not describe this dispatched compensation.")
                    { IsConflictingEvidence = true };
        }
        return state;
    }
}
