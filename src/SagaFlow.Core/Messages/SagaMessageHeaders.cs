namespace SagaFlow.Core.Messages;

/// <summary>Immutable execution context owned by the local engine, echoed by participants.</summary>
public sealed record SagaMessageHeaders
{
    public int ProtocolVersion { get; init; }
    public string ProcessDomain { get; init; } = string.Empty;
    public string ProcessName { get; init; } = string.Empty;
    public string ProcessVersion { get; init; } = string.Empty;
    public string ExecutionId { get; init; } = string.Empty;
    public string CommandId { get; init; } = string.Empty;
    public string CommandName { get; init; } = string.Empty;
    public string? CausationId { get; init; }
    public string? ForwardExecutionId { get; init; }
    public string? ReplyDestination { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string SagaInstanceId { get; init; } = string.Empty;

    public string CorrelationId { get; init; } = string.Empty;
    public string StepId { get; init; } = string.Empty;
    public bool IsCompensation { get; init; }
}
