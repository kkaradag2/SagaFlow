using System.Text.Json;

namespace SagaFlow.Core.Messages;

public sealed class SagaOutcomeEvent
{
    public string Name { get; init; } = string.Empty;

    public bool IsSuccess { get; init; }
    public JsonElement Result { get; init; } = JsonSerializer.SerializeToElement<object?>(null);

    public SagaMessageHeaders Headers { get; init; } = new();
}
