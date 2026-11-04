using SagaFlow.Core.Messages;
namespace SagaFlow.Core.State;

/// <summary>Progress of the step's Invocation request. Identity/payload live only in that request.</summary>
public sealed record InvocationProgress(ExternalProcessStatus Status, string? TargetSagaInstanceId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? LastObservedAt);

/// <summary>Source linkage owned by the accepting local target instance. No access to source state is required.</summary>
public sealed record IncomingInvocation(ProcessInvocationIdentity Identity, DateTimeOffset CreatedAt, DateTimeOffset AcceptedAt, System.Text.Json.JsonElement? Payload = null);

/// <summary>Local publish attempt only. Requested does not assert broker confirmation or business acceptance.</summary>
public sealed record InvocationPublication(string? ActiveAttemptId, DateTimeOffset AttemptedAt, string? LastError);
/// <summary>Sticky admission decision. Validation may run concurrently, but the first committed decision wins.</summary>
public sealed record IncomingInvocationDecision(ProcessInvocationRequest Request, string? RejectionReason, InvocationMessage Reply);

public sealed record InvocationObservedMessage(string MessageId, string Fingerprint);

/// <summary>Remote activity cannot be assumed rolled back when local forward publication fails.</summary>
public sealed record InvocationReconciliation(string Reason, DateTimeOffset RequiredAt);
