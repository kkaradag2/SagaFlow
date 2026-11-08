using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RabbitMQ.Client;

namespace SagaFlow.Participants.Messaging;

// Sample-local RabbitMQ infrastructure, with no SagaFlow package or execution algorithm.
public sealed record ParticipantContext
{
    public string SagaInstanceId { get; init; } = "";
    public string CorrelationId { get; init; } = "";
    public string StepId { get; init; } = "";
    public bool IsCompensation { get; init; }
    [JsonIgnore] internal IReadOnlyDictionary<string, object?> Headers { get; init; }
        = new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>());

    public bool SameExecution(ParticipantContext other) => SagaInstanceId == other.SagaInstanceId &&
        CorrelationId == other.CorrelationId && StepId == other.StepId && IsCompensation == other.IsCompensation &&
        Headers.Count == other.Headers.Count && Headers.All(pair => other.Headers.TryGetValue(pair.Key, out var value) &&
            (pair.Value is byte[] a && value is byte[] b ? a.AsSpan().SequenceEqual(b) : Equals(pair.Value, value)));
}

public sealed class ParticipantCommand
{
    public string Name { get; init; } = "";
    public JsonElement Payload { get; init; }
    public ParticipantContext Headers { get; init; } = new();
}

public sealed class ParticipantOutcome
{
    public string MessageId { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public bool IsSuccess { get; init; }
    public JsonElement Result { get; init; }
    public ParticipantContext Headers { get; init; } = new();
}

public static class ParticipantProtocol
{
    private static string Text(IReadOnlyDictionary<string, object?> headers, string name)
    {
        if (!headers.TryGetValue(name, out var value)) throw new JsonException($"Missing {name}.");
        string? text;
        try { text = value switch { string s => s, byte[] bytes => new UTF8Encoding(false, true).GetString(bytes), _ => null }; }
        catch (DecoderFallbackException error) { throw new JsonException($"Invalid UTF-8 in {name}.", error); }
        return !string.IsNullOrWhiteSpace(text) ? text : throw new JsonException($"Invalid {name}.");
    }

    public static ParticipantCommand ReadCommand(ReadOnlySpan<byte> body, IReadOnlyBasicProperties properties)
    {
        var copied = (properties.Headers ?? throw new JsonException("Execution context required."))
            .Where(pair => pair.Key == "sf-version" || pair.Key.StartsWith("sf-ex-", StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value is byte[] bytes ? bytes.ToArray() : pair.Value);
        var headers = new ReadOnlyDictionary<string, object?>(copied);
        if (Text(headers, "sf-version") != "1" || properties.ContentType != "application/json")
            throw new JsonException("Only BPI-1A version 1 JSON commands are accepted.");
        foreach (var key in new[] { "sf-ex-domain", "sf-ex-process", "sf-ex-process-version", "sf-ex-execution-id", "sf-ex-created-at" })
            _ = Text(headers, key);
        var kind = Text(headers, "sf-ex-kind");
        if (kind is not ("forward" or "compensation")) throw new JsonException("Invalid execution kind.");
        if (kind == "compensation") _ = Text(headers, "sf-ex-forward-execution-id");
        if (Text(headers, "sf-ex-command-id") != properties.MessageId ||
            Text(headers, "sf-ex-command-type") != properties.Type ||
            Text(headers, "sf-ex-reply-to") != properties.ReplyTo ||
            Text(headers, "sf-ex-correlation-id") != properties.CorrelationId)
            throw new JsonException("Command properties conflict with execution context.");
        return new()
        {
            Name = properties.Type!, Payload = JsonSerializer.Deserialize<JsonElement>(body),
            Headers = new()
            {
                SagaInstanceId = Text(headers, "sf-ex-saga-id"), CorrelationId = properties.CorrelationId!,
                StepId = Text(headers, "sf-ex-step-id"), IsCompensation = kind == "compensation", Headers = headers
            }
        };
    }

    public static BasicProperties OutcomeProperties(ParticipantOutcome outcome)
    {
        var context = outcome.Headers;
        var headers = context.Headers.ToDictionary(pair => pair.Key,
            pair => pair.Value is byte[] bytes ? bytes.ToArray() : pair.Value);
        headers["sf-causation-id"] = Text(context.Headers, "sf-ex-command-id");
        headers["sf-outcome"] = outcome.IsSuccess ? "success" : "failure";
        return new()
        {
            ContentType = "application/json", ContentEncoding = "utf-8", DeliveryMode = DeliveryModes.Persistent,
            Type = outcome.Name, MessageId = outcome.MessageId, CorrelationId = context.CorrelationId,
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds()), Headers = headers
        };
    }
}
