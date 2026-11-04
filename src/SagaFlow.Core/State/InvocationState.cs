using SagaFlow.Core.Messages;
namespace SagaFlow.Core.State;

/// <summary>Progress of the step's Invocation request. Identity/payload live only in that request.</summary>
public sealed record InvocationProgress(ExternalProcessStatus Status, string? TargetSagaInstanceId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? LastObservedAt);

/// <summary>Source linkage owned by the accepting local target instance. No access to source state is required.</summary>
public sealed record IncomingInvocation(ProcessInvocationIdentity Identity, DateTimeOffset CreatedAt, DateTimeOffset AcceptedAt, System.Text.Json.JsonElement? Payload = null);
