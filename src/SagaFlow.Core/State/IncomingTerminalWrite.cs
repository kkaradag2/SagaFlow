using SagaFlow.Core.Messages;

namespace SagaFlow.Core.State;

/// <summary>Validates the incoming terminal invariant before a store commits a working copy.</summary>
public readonly record struct IncomingTerminalWrite(SagaStatus Status, ProcessInvocationTerminalOutcome? Response, bool Queued)
{
    public static IncomingTerminalWrite Capture(SagaInstance instance) =>
        new(instance.Status, instance.InvocationTerminalOutcome, instance.InvocationTerminalQueued);

    public static ExternalProcessStatus? ResponseStatus(SagaStatus status) => status switch
    {
        SagaStatus.Completed => ExternalProcessStatus.Completed,
        SagaStatus.Failed => ExternalProcessStatus.Failed,
        SagaStatus.Compensated => ExternalProcessStatus.Compensated,
        _ => null
    };

    public void Validate(SagaInstance after, OutboxMessage? message = null)
    {
        if (after.IncomingInvocation is not { } incoming) return;
        var terminalStatus = ResponseStatus(after.Status);
        var terminalMessage = message?.Name == nameof(ProcessInvocationTerminalOutcome) ||
            (message?.Invocation?.Status is ExternalProcessStatus.Completed or ExternalProcessStatus.Failed or ExternalProcessStatus.Compensated);

        if (ResponseStatus(Status) is { } previousStatus)
        {
            if (!Queued || Response is null || Response.Status != previousStatus ||
                Response.Message.Identity != incoming.Identity || Response.TargetSagaInstanceId != after.Id || after.Status != Status ||
                after.InvocationTerminalOutcome != Response || !after.InvocationTerminalQueued || terminalMessage)
                throw new InvalidOperationException("An incoming terminal status and its queued response are immutable.");
            return;
        }

        if (terminalStatus is null)
        {
            if (after.InvocationTerminalQueued || after.InvocationTerminalOutcome is not null || terminalMessage)
                throw new InvalidOperationException("A nonterminal incoming Saga cannot record a Terminal response.");
            return;
        }

        if (!after.InvocationTerminalQueued || after.InvocationTerminalOutcome is not { } response ||
            response.Status != terminalStatus || response.Message.Identity != incoming.Identity ||
            response.TargetSagaInstanceId != after.Id || message?.Invocation is not { } invocation ||
            invocation.Message != response.Message || invocation.Status != response.Status ||
            invocation.TargetSagaInstanceId != after.Id || invocation.Reason is not null ||
            !message.Body.AsSpan().SequenceEqual("null"u8))
            throw new InvalidOperationException("An incoming Saga must commit its terminal transition with the matching Terminal outbox message.");
    }
}
