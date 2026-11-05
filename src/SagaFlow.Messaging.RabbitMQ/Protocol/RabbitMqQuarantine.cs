using System.Text;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace SagaFlow.RabbitMq.Protocol;

/// <summary>AMQP-only source helper, also compiled into the sample transport (no SagaFlow assembly dependency).</summary>
internal static class RabbitMqQuarantine
{
    internal static string Queue(string sourceQueue) => sourceQueue + ".quarantine";

    internal static bool HasSupportedVersion(IReadOnlyBasicProperties properties)
    {
        if (properties.Headers is null || !properties.Headers.TryGetValue("sf-version", out var value)) return false;
        try { return value switch { string text => text == "1", byte[] bytes => new UTF8Encoding(false, true).GetString(bytes) == "1", _ => false }; }
        catch (DecoderFallbackException) { return false; }
    }

    internal static async Task DeclareAsync(IChannel channel, string sourceQueue, CancellationToken ct) =>
        _ = await channel.QueueDeclareAsync(Queue(sourceQueue), durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);

    /// <summary>Caller must use publisher confirms AND confirmation tracking, and ACK the source only after this returns.
    /// A lost confirm/ACK can yield multiple preserved copies. No automatic replay or dedup is performed here.</summary>
    internal static ValueTask TransferAsync(IChannel publisher, string sourceQueue, BasicDeliverEventArgs delivery, CancellationToken ct) =>
        TransferAsync(publisher, sourceQueue, delivery.Exchange, delivery.RoutingKey, delivery.BasicProperties, delivery.Body, ct);

    internal static ValueTask TransferAsync(IChannel publisher, string sourceQueue, string exchange, string routingKey,
        IReadOnlyBasicProperties metadata, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        var properties = new BasicProperties(metadata)
        {
            DeliveryMode = DeliveryModes.Persistent,
            // Retain the original TTL for inspection; it must not expire preserved legacy commands.
            Expiration = null,
            Headers = metadata.Headers?.ToDictionary(pair => pair.Key, pair => pair.Value) ?? new()
        };
        properties.Headers["sf-quarantine-source"] = sourceQueue;
        properties.Headers["sf-quarantine-exchange"] = exchange;
        properties.Headers["sf-quarantine-routing-key"] = routingKey;
        properties.Headers["sf-quarantine-reason"] = "missing-or-unsupported-wire-version";
        if (metadata.Expiration is not null) properties.Headers["sf-quarantine-original-expiration"] = metadata.Expiration;
        return publisher.BasicPublishAsync("", Queue(sourceQueue), mandatory: true, properties, body, ct);
    }
}
