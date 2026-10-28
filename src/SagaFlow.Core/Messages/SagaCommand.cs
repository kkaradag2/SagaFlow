using System.Text.Json;

namespace SagaFlow.Core.Messages;

public sealed class SagaCommand
{
    public string Name { get; init; } = string.Empty;
    public JsonElement Payload { get; init; } = JsonSerializer.SerializeToElement<object?>(null);
    public TimeSpan? TimeToLive { get; init; }

    public SagaMessageHeaders Headers { get; init; } = new();
}
