namespace SagaFlow.Core.Messages;

public sealed class SagaCommand
{
    public string Name { get; init; } = string.Empty;

    public SagaMessageHeaders Headers { get; init; } = new();
}
