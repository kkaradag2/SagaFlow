using System.Text.Json;
using SagaFlow.Core.Messages;

namespace SagaFlow.Messaging.RabbitMQ;

internal static class RabbitMqMessageSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static byte[] Serialize(SagaCommand command) =>
        JsonSerializer.SerializeToUtf8Bytes(command, Options);

    public static byte[] Serialize(SagaOutcomeEvent outcome) =>
        JsonSerializer.SerializeToUtf8Bytes(outcome, Options);

    public static SagaCommand DeserializeCommand(ReadOnlySpan<byte> body) =>
        JsonSerializer.Deserialize<SagaCommand>(body, Options)
        ?? throw new JsonException("RabbitMQ command payload is null.");

    public static SagaOutcomeEvent DeserializeOutcome(ReadOnlySpan<byte> body) =>
        JsonSerializer.Deserialize<SagaOutcomeEvent>(body, Options)
        ?? throw new JsonException("RabbitMQ outcome payload is null.");
}
