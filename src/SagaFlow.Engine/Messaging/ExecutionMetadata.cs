using SagaFlow.Core.Messages;
using SagaFlow.Core.State;
using SagaFlow.Engine.Coordination;

namespace SagaFlow.Engine.Messaging;

internal static class ExecutionMetadata
{
    // Created once inside the atomic dispatch claim. No retry/attempt identity is implied.
    internal static SagaMessageHeaders Create(SagaInstance instance, string step, string command,
        DateTimeOffset createdAt, string? reply, bool compensation = false) => new()
    {
        ProtocolVersion = 1, SagaInstanceId = instance.Id, CorrelationId = instance.CorrelationId,
        ProcessDomain = instance.DefinitionDomain, ProcessName = instance.DefinitionName,
        ProcessVersion = instance.DefinitionVersion, StepId = step, CommandName = command,
        ExecutionId = Guid.NewGuid().ToString("N"), CommandId = Guid.NewGuid().ToString("N"),
        CreatedAt = createdAt, ReplyDestination = reply, IsCompensation = compensation,
        ForwardExecutionId = compensation ? instance.Steps.Single(s => s.StepId == step).Execution?.ExecutionId : null
    };

    internal static void Validate(SagaMessageHeaders? expected, SagaOutcomeEvent outcome)
    {
        // Historical in-process state can lack an execution context. The versioned wire reader
        // never admits missing context; it cannot upgrade an old envelope into a new execution.
        if (expected is null && outcome.Headers.ProtocolVersion != 0 ||
            expected is not null && (expected != outcome.Headers ||
            outcome.CausationId is not null && outcome.CausationId != expected.CommandId))
            throw new InvalidSagaOutcomeException("Outcome execution context does not match the stored dispatch.");
    }
}
