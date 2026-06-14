namespace SagaFlow.Core.Messages;

public sealed class SagaMessageHeaders
{
    public string SagaInstanceId { get; init; } = string.Empty;

    public string CorrelationId { get; init; } = string.Empty;
}
