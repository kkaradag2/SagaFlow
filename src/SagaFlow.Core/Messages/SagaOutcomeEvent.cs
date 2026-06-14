namespace SagaFlow.Core.Messages;

public sealed class SagaOutcomeEvent
{
    public string Name { get; init; } = string.Empty;

    public bool IsSuccess { get; init; }

    public SagaMessageHeaders Headers { get; init; } = new();
}
