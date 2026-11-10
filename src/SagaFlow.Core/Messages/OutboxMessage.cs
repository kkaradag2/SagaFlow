using System.Text.Json;

namespace SagaFlow.Core.Messages;

/// <summary>The immutable command bytes and execution context committed with a Saga transition.</summary>
public sealed record OutboxMessage
{
    public required string MessageId { get; init; }
    public required string Name { get; init; }
    public required string Destination { get; init; }
    public string? Exchange { get; init; }
    public required byte[] Body { get; init; }
    public SagaMessageHeaders Headers { get; init; } = new();
    public byte[] RecoverySnapshot { get; init; } = [];
    public InvocationOutbox? Invocation { get; init; }
    public string? ReplyDestination { get; init; }
    public OutboxWireProperties? WireProperties { get; init; }
    public TimeSpan? TimeToLive { get; init; }
    /// <summary>A forward command may be held until its local preparation finishes.</summary>
    public string? PreparationOwner { get; init; }
    public DateTimeOffset CreatedAt => Invocation?.Message.OccurredAt ?? Headers.CreatedAt;
    public string SagaInstanceId => Invocation?.LocalReference ?? Headers.SagaInstanceId;

    public void ValidateFor(string localReference)
    {
        if (string.IsNullOrWhiteSpace(MessageId) || string.IsNullOrWhiteSpace(Destination) ||
            SagaInstanceId != localReference || Body.Length == 0)
            throw new InvalidOperationException("Invalid outbox identity, owner, or route.");
        if (Invocation is { } invocation)
        {
            if (MessageId != invocation.Message.MessageId || Name != ToInvocation().GetType().Name ||
                (PreparationOwner is not null && invocation.Status != ExternalProcessStatus.Accepted) ||
                TimeToLive is not null || RecoverySnapshot.Length != 0)
                throw new InvalidOperationException("Outbox message does not match the invocation.");
        }
        else if (MessageId != Headers.CommandId || Destination != Headers.StepId || Name != Headers.CommandName ||
                 RecoverySnapshot.Length is 0 or > SagaCommand.MaximumRecoverySnapshotBytes)
            throw new InvalidOperationException("Outbox message does not match the Saga command.");
    }

    public void ValidateAdmission(SagaFlow.Core.State.IncomingInvocationDecision decision, string? targetId)
    {
        if (Invocation is not { } invocation || invocation.Message != decision.Reply ||
            invocation.TargetSagaInstanceId != targetId || invocation.Reason != decision.RejectionReason ||
            invocation.Status != (targetId is null ? ExternalProcessStatus.Rejected : ExternalProcessStatus.Accepted))
            throw new InvalidOperationException("Outbox response does not match the admission decision.");
        ValidateFor(targetId ?? decision.Request.Identity.InvocationId);
    }

    public static OutboxMessage FromInvocation(object value)
    {
        var (message, targetId, status, reason, body) = value switch
        {
            ProcessInvocationRequest r => (r.Message, (string?)null, ExternalProcessStatus.Requested, (string?)null, JsonSerializer.SerializeToUtf8Bytes(r.Payload)),
            ProcessInvocationAccepted a => (a.Message, a.TargetSagaInstanceId, ExternalProcessStatus.Accepted, null, "null"u8.ToArray()),
            ProcessInvocationRejected r => (r.Message, null, ExternalProcessStatus.Rejected, r.Reason, "null"u8.ToArray()),
            ProcessInvocationTerminalOutcome t => (t.Message, t.TargetSagaInstanceId, t.Status, null, "null"u8.ToArray()),
            _ => throw new ArgumentException("Unsupported invocation message.", nameof(value))
        };
        var invocation = new InvocationOutbox(message, targetId, status, reason);
        return new()
        {
            MessageId = message.MessageId, Name = value.GetType().Name, Body = body,
            Destination = invocation.Destination.Domain, Invocation = invocation
        };
    }

    public object ToInvocation()
    {
        var value = Invocation ?? throw new InvalidOperationException("Outbox message is not an invocation.");
        return value.Status switch
        {
            ExternalProcessStatus.Requested => new ProcessInvocationRequest(value.Message.Identity, JsonSerializer.Deserialize<JsonElement>(Body), value.Message),
            ExternalProcessStatus.Accepted => new ProcessInvocationAccepted(value.Message, value.TargetSagaInstanceId!),
            ExternalProcessStatus.Rejected => new ProcessInvocationRejected(value.Message, value.Reason!),
            _ => new ProcessInvocationTerminalOutcome(value.Message, value.TargetSagaInstanceId!, value.Status)
        };
    }

    public static OutboxMessage FromCommand(SagaCommand command, string? exchange = null) => new()
    {
        MessageId = command.Headers.CommandId,
        Name = command.Name,
        Destination = command.Headers.StepId,
        Exchange = exchange,
        Body = JsonSerializer.SerializeToUtf8Bytes(command.Payload),
        Headers = command.Headers,
        RecoverySnapshot = command.RecoverySnapshot?.ToArray() ?? throw new InvalidOperationException("Outbox command requires recovery evidence."),
        TimeToLive = command.TimeToLive
    };

    public SagaCommand ToCommand() => new()
    {
        Name = Name, Headers = Headers, Payload = JsonSerializer.Deserialize<JsonElement>(Body),
        RecoverySnapshot = RecoverySnapshot.ToArray(), TimeToLive = TimeToLive
    };
}

public sealed record InvocationOutbox(InvocationMessage Message, string? TargetSagaInstanceId, ExternalProcessStatus Status, string? Reason)
{
    public bool IsRequest => Status == ExternalProcessStatus.Requested;
    public ProcessIdentity Publisher => IsRequest ? Message.Identity.Source : Message.Identity.Target;
    public ProcessIdentity Destination => IsRequest ? Message.Identity.Target : Message.Identity.Source;
    public string LocalReference => IsRequest ? Message.Identity.SourceSagaInstanceId : TargetSagaInstanceId ?? Message.Identity.InvocationId;
}

/// <summary>Transport properties captured at enqueue time. The business body remains in OutboxMessage.Body.</summary>
public sealed record OutboxWireProperties(string ContentType, string ContentEncoding, string CorrelationId,
    bool Persistent, IReadOnlyDictionary<string, string> Headers);

public sealed record OutboxClaim(OutboxMessage Message, string Owner, int Attempts, DateTimeOffset LeaseUntil);

public sealed class OutboxClaimLostException() : InvalidOperationException("Outbox claim is no longer owned by this dispatcher.");
