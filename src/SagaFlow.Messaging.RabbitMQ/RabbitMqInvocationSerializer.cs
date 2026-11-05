using System.Globalization;
using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using SagaFlow.Core.Messages;

namespace SagaFlow.Messaging.RabbitMQ;

internal static class RabbitMqInvocationSerializer
{
    internal static byte[] Body<T>(T value) => value is ProcessInvocationRequest request
        ? JsonSerializer.SerializeToUtf8Bytes(request.Payload) : "null"u8.ToArray();

    internal static BasicProperties Properties<T>(T value, InvocationMessage message)
    {
        var id = message.Identity;
        var h = new Dictionary<string, object?> { ["sf-version"] = "1", ["sf-inv-kind"] = typeof(T).Name,
            ["sf-inv-id"] = id.InvocationId, ["sf-inv-source-saga"] = id.SourceSagaInstanceId,
            ["sf-inv-source-step"] = id.SourceStep, ["sf-inv-occurred-at"] = message.OccurredAt.ToString("O", CultureInfo.InvariantCulture) };
        void Process(string prefix, ProcessIdentity process)
        { h[prefix + "domain"] = process.Domain; h[prefix + "process"] = process.Process; h[prefix + "version"] = process.Version; }
        Process("sf-inv-source-", id.Source); Process("sf-inv-target-", id.Target);
        if (id.SourceExecutionId is not null) h["sf-inv-source-execution"] = id.SourceExecutionId;
        if (message.CausationId is not null) h["sf-inv-causation-id"] = message.CausationId;
        switch (value)
        {
            case ProcessInvocationAccepted a: h["sf-inv-target-saga"] = a.TargetSagaInstanceId; h["sf-inv-status"] = "Accepted"; break;
            case ProcessInvocationRejected r: h["sf-inv-status"] = "Rejected"; h["sf-inv-reason"] = r.Reason; break;
            case ProcessInvocationTerminalOutcome t: h["sf-inv-target-saga"] = t.TargetSagaInstanceId; h["sf-inv-status"] = t.Status.ToString(); break;
        }
        return new() { ContentType = "application/json", ContentEncoding = "utf-8", DeliveryMode = DeliveryModes.Persistent,
            Type = typeof(T).Name, MessageId = message.MessageId, CorrelationId = id.CorrelationId,
            ReplyTo = value is ProcessInvocationRequest ? RabbitMqInvocationTransport.Queue(id.Source, false) : null, Headers = h };
    }

    internal static T Read<T>(ReadOnlySpan<byte> body, IReadOnlyBasicProperties p)
    {
        string? Optional(string key)
        {
            if (p.Headers is null || !p.Headers.TryGetValue(key, out var v)) return null;
            string? text;
            try { text = v switch { string s => s, byte[] b => new UTF8Encoding(false, true).GetString(b), _ => null }; }
            catch (DecoderFallbackException error) { throw new JsonException("Invalid invocation UTF-8.", error); }
            return !string.IsNullOrWhiteSpace(text) ? text : throw new JsonException("Invalid " + key);
        }
        string Required(string key) => Optional(key) ?? throw new JsonException("Missing " + key);
        ProcessIdentity Process(string prefix) => new(Required(prefix + "domain"), Required(prefix + "process"), Required(prefix + "version"));
        if (Required("sf-version") != "1" || Required("sf-inv-kind") != typeof(T).Name || p.Type != typeof(T).Name || p.ContentType != "application/json")
            throw new JsonException("Invocation version/type mismatch.");
        if (!DateTimeOffset.TryParseExact(Required("sf-inv-occurred-at"), "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var occurred)
            || occurred == default || occurred.Offset != TimeSpan.Zero) throw new JsonException("Invalid invocation event time.");
        var identity = new ProcessInvocationIdentity(Required("sf-inv-id"), p.CorrelationId!, Process("sf-inv-source-"),
            Required("sf-inv-source-saga"), Required("sf-inv-source-step"), Process("sf-inv-target-"), Optional("sf-inv-source-execution"));
        var message = new InvocationMessage(identity, p.MessageId!, occurred, Optional("sf-inv-causation-id"));
        var json = JsonSerializer.Deserialize<JsonElement>(body);
        object value;
        if (typeof(T) == typeof(ProcessInvocationRequest))
        {
            if (p.ReplyTo != RabbitMqInvocationTransport.Queue(identity.Source, false) || Optional("sf-inv-target-saga") is not null || Optional("sf-inv-status") is not null)
                throw new JsonException("Invalid request routing/target status.");
            value = new ProcessInvocationRequest(identity, json, message);
        }
        else
        {
            if (json.ValueKind != JsonValueKind.Null || p.ReplyTo is not null) throw new JsonException("Lifecycle body must be JSON null with no reply property.");
            var status = Required("sf-inv-status");
            value = typeof(T) == typeof(ProcessInvocationAccepted) && status == "Accepted" ? new ProcessInvocationAccepted(message, Required("sf-inv-target-saga"))
                : typeof(T) == typeof(ProcessInvocationRejected) && status == "Rejected" && Optional("sf-inv-target-saga") is null ? new ProcessInvocationRejected(message, Required("sf-inv-reason"))
                : typeof(T) == typeof(ProcessInvocationTerminalOutcome) && Enum.TryParse<ExternalProcessStatus>(status, out var terminal)
                    ? new ProcessInvocationTerminalOutcome(message, Required("sf-inv-target-saga"), terminal)
                : throw new JsonException("Invalid invocation lifecycle status.");
        }
        return (T)value;
    }
}
