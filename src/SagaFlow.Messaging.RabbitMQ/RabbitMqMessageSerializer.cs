using System.Globalization;
using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using SagaFlow.Core.Messages;

namespace SagaFlow.Messaging.RabbitMQ;

/// <summary>BPI-1A v1. Body is exclusively application JSON; no legacy envelope guessing.</summary>
internal static class RabbitMqMessageSerializer
{
    private static readonly HashSet<string> ExecutionHeaders = new(StringComparer.Ordinal)
    {
        "sf-ex-saga-id", "sf-ex-correlation-id", "sf-ex-domain", "sf-ex-process", "sf-ex-process-version",
        "sf-ex-step-id", "sf-ex-execution-id", "sf-ex-command-id", "sf-ex-command-type", "sf-ex-kind",
        "sf-ex-created-at", "sf-ex-reply-to", "sf-ex-forward-execution-id", "sf-ex-cause-id",
        "sf-ex-state-snapshot"
    };
    public static byte[] Serialize(SagaCommand command) => JsonSerializer.SerializeToUtf8Bytes(command.Payload);
    public static byte[] Serialize(SagaOutcomeEvent outcome) => JsonSerializer.SerializeToUtf8Bytes(outcome.Result);

    public static BasicProperties CommandProperties(SagaCommand command)
    {
        var properties = Properties(command.Headers, command.Name,
            command.Headers.CommandId, command.Headers.CausationId, command.Headers.ReplyDestination);
        _ = ReadContext(properties);
        if (command.Name != command.Headers.CommandName)
            throw new JsonException("Command type conflicts with its execution context.");
        if (command.RecoverySnapshot is { } snapshot)
        {
            if (snapshot.Length is 0 or > SagaCommand.MaximumRecoverySnapshotBytes)
                throw new JsonException("Execution snapshot exceeds the AMQP metadata limit.");
            properties.Headers!["sf-ex-state-snapshot"] = snapshot.ToArray();
        }
        properties.Timestamp = new AmqpTimestamp(command.Headers.CreatedAt.ToUnixTimeSeconds());
        return properties;
    }

    public static BasicProperties OutcomeProperties(SagaOutcomeEvent outcome)
    {
        var properties = Properties(outcome.Headers, string.IsNullOrWhiteSpace(outcome.Name) ? "outcome" : outcome.Name,
            outcome.MessageId, outcome.CausationId ?? outcome.Headers.CommandId, null);
        properties.Headers!["sf-outcome"] = outcome.IsSuccess ? "success" : "failure";
        if (outcome.RecoverySnapshot is { } snapshot) properties.Headers["sf-ex-state-snapshot"] = snapshot.ToArray();
        return properties;
    }

    private static BasicProperties Properties(SagaMessageHeaders h, string type, string id, string? cause, string? reply)
    {
        if (h.ProtocolVersion != 1) throw new JsonException("Only BPI-1A version 1 is supported.");
        var headers = new Dictionary<string, object?>
        {
            ["sf-version"] = "1", ["sf-ex-saga-id"] = h.SagaInstanceId,
            ["sf-ex-correlation-id"] = h.CorrelationId, ["sf-ex-domain"] = h.ProcessDomain,
            ["sf-ex-process"] = h.ProcessName, ["sf-ex-process-version"] = h.ProcessVersion,
            ["sf-ex-step-id"] = h.StepId, ["sf-ex-execution-id"] = h.ExecutionId,
            ["sf-ex-command-id"] = h.CommandId, ["sf-ex-command-type"] = h.CommandName,
            ["sf-ex-kind"] = h.IsCompensation ? "compensation" : "forward",
            ["sf-ex-created-at"] = h.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            ["sf-ex-reply-to"] = h.ReplyDestination
        };
        if (h.ForwardExecutionId is not null) headers["sf-ex-forward-execution-id"] = h.ForwardExecutionId;
        if (h.CausationId is not null) headers["sf-ex-cause-id"] = h.CausationId;
        if (cause is not null) headers["sf-causation-id"] = cause;
        return new()
        {
            ContentType = "application/json", ContentEncoding = "utf-8", DeliveryMode = DeliveryModes.Persistent,
            Type = type, MessageId = id, CorrelationId = h.CorrelationId, ReplyTo = reply,
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds()), Headers = headers
        };
    }

    public static SagaCommand DeserializeCommand(BasicGetResult delivery) => DeserializeCommand(delivery.Body.Span, delivery.BasicProperties);
    public static SagaCommand DeserializeCommand(ReadOnlySpan<byte> body, IReadOnlyBasicProperties properties)
    {
        var headers = ReadContext(properties);
        if (properties.MessageId != headers.CommandId || properties.Type != headers.CommandName ||
            properties.ReplyTo != headers.ReplyDestination || Optional(properties, "sf-causation-id") != headers.CausationId)
            throw new JsonException("Command properties conflict with its execution context.");
        return new()
        {
            Name = properties.Type!, Headers = headers, Payload = JsonSerializer.Deserialize<JsonElement>(body),
            TimeToLive = properties.Expiration is null ? null : TimeSpan.FromMilliseconds(double.Parse(properties.Expiration, CultureInfo.InvariantCulture))
        };
    }

    public static SagaOutcomeEvent DeserializeOutcome(ReadOnlySpan<byte> body, IReadOnlyBasicProperties properties)
    {
        var headers = ReadContext(properties);
        var status = Required(properties, "sf-outcome");
        if (status is not ("success" or "failure") || Required(properties, "sf-causation-id") != headers.CommandId)
            throw new JsonException("Outcome status/causation is invalid.");
        return new()
        {
            Name = properties.Type!, MessageId = properties.MessageId!, CausationId = headers.CommandId,
            Headers = headers, IsSuccess = status == "success", Result = JsonSerializer.Deserialize<JsonElement>(body),
            RecoverySnapshot = properties.Headers is not null &&
                properties.Headers.TryGetValue("sf-ex-state-snapshot", out var rawSnapshot) && rawSnapshot is byte[] snapshot
                ? snapshot.ToArray() : null
        };
    }

    private static SagaMessageHeaders ReadContext(IReadOnlyBasicProperties p)
    {
        if (Required(p, "sf-version") != "1") throw new JsonException("Unsupported SagaFlow wire version; legacy envelopes are not accepted.");
        if (p.Headers!.Keys.Any(key => key.StartsWith("sf-ex-", StringComparison.Ordinal) && !ExecutionHeaders.Contains(key)))
            throw new JsonException("Unknown execution header for wire version 1.");
        if (p.ContentType != "application/json" || string.IsNullOrWhiteSpace(p.Type) || string.IsNullOrWhiteSpace(p.MessageId))
            throw new JsonException("JSON content type, message ID and type are required.");
        var kind = Required(p, "sf-ex-kind");
        if (kind is not ("forward" or "compensation")) throw new JsonException("Unknown execution kind.");
        if (!DateTimeOffset.TryParseExact(Required(p, "sf-ex-created-at"), "O", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var created) || created == default || created.Offset != TimeSpan.Zero)
            throw new JsonException("Execution creation time must be UTC in round-trip format.");
        var h = new SagaMessageHeaders
        {
            ProtocolVersion = 1, SagaInstanceId = Required(p, "sf-ex-saga-id"), CorrelationId = Required(p, "sf-ex-correlation-id"),
            ProcessDomain = Required(p, "sf-ex-domain"), ProcessName = Required(p, "sf-ex-process"),
            ProcessVersion = Required(p, "sf-ex-process-version"), StepId = Required(p, "sf-ex-step-id"),
            ExecutionId = Required(p, "sf-ex-execution-id"), CommandId = Required(p, "sf-ex-command-id"),
            CommandName = Required(p, "sf-ex-command-type"), IsCompensation = kind == "compensation", CreatedAt = created,
            ReplyDestination = Required(p, "sf-ex-reply-to"), CausationId = Optional(p, "sf-ex-cause-id"),
            ForwardExecutionId = kind == "compensation" ? Required(p, "sf-ex-forward-execution-id") : Optional(p, "sf-ex-forward-execution-id")
        };
        if (p.CorrelationId != h.CorrelationId) throw new JsonException("Correlation property conflicts with execution context.");
        return h;
    }

    private static string Required(IReadOnlyBasicProperties p, string key) => Optional(p, key)
        ?? throw new JsonException($"Missing or invalid {key} header.");
    private static string? Optional(IReadOnlyBasicProperties p, string key)
    {
        if (p.Headers is null || !p.Headers.TryGetValue(key, out var value)) return null;
        string? text;
        try { text = value switch { string s => s, byte[] bytes => new UTF8Encoding(false, true).GetString(bytes), _ => null }; }
        catch (DecoderFallbackException error) { throw new JsonException($"Invalid UTF-8 in {key}.", error); }
        if (string.IsNullOrWhiteSpace(text)) throw new JsonException($"Invalid {key} header type/value.");
        return text;
    }
}
