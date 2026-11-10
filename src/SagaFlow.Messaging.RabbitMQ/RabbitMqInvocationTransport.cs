using SagaFlow.RabbitMq.Protocol;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Messages;
using SagaFlow.Engine.Messaging;
using SagaFlow.Engine.ProcessDefinition;

namespace SagaFlow.Messaging.RabbitMQ;

/// <summary>Opt-in domain process traffic. Never uses discovery origins or participant queues.</summary>
internal sealed class RabbitMqInvocationTransport(RabbitMqConnectionProvider connections, ProcessDefinitionRegistry definitions,
    IServiceScopeFactory scopes, ILogger<RabbitMqInvocationTransport> logger) : IProcessInvocationBus, IProcessInvocationReconciliation, IHostedService, IAsyncDisposable
{
    internal const string Exchange = "sagaflow.process-invocations";
    private readonly SemaphoreSlim publishing = new(1, 1);
    private readonly SemaphoreSlim stopping = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private IChannel? publisher;
    private IChannel? consumer;
    private IChannel? parking;
    private readonly SemaphoreSlim reconciling = new(1, 1);
    private string DeferredQueue => Queue(local ?? throw new InvalidOperationException("Invocation transport is not started."), false) + ".deferred";
    private ProcessIdentity? local;
    private readonly List<string> tags = [];
    private long handledMessages;
    private long rejectedMessages;
    private long deferredReplays;
    private long parkingFailures;
    private Task? quarantineShutdown;
    internal long ParkingFailureCount => Interlocked.Read(ref parkingFailures);
    internal long DeferredReplayCount => Interlocked.Read(ref deferredReplays);
    internal long RejectedMessageCount => Interlocked.Read(ref rejectedMessages);
    internal long HandledMessageCount => Interlocked.Read(ref handledMessages);
    public static string Queue(ProcessIdentity identity, bool request) => "sagaflow.invocations."
        + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(identity)))
        + (request ? ".requests" : ".outcomes");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var locals = definitions.Snapshot().Where(d => d.Origin == DefinitionOrigin.Local).ToArray();
        if (locals.Length != 1) throw new InvalidOperationException("Invocation transport requires exactly one locally owned process/version.");
        var definition = locals[0].Definition;
        local = new(definition.Domain, definition.Name, definition.Version);
        if (Queue(local, true).Length > 255 || Queue(local, false).Length > 255) throw new InvalidOperationException("Invocation routing identity exceeds AMQP queue name limit.");
        var connection = await connections.GetConnectionAsync(cancellationToken);
        publisher = await connection.CreateChannelAsync(new CreateChannelOptions(true, true), cancellationToken);
        consumer = await connection.CreateChannelAsync(new CreateChannelOptions(false, false, consumerDispatchConcurrency: 1), cancellationToken);
        parking = await connection.CreateChannelAsync(new CreateChannelOptions(false, false), cancellationToken);
        await RabbitMqQuarantine.DeclareAsync(parking, DeferredQueue, cancellationToken);
        await parking.QueueDeclareAsync(DeferredQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: cancellationToken);
        await consumer.ExchangeDeclareAsync(Exchange, ExchangeType.Direct, durable: true, cancellationToken: cancellationToken);
        await consumer.BasicQosAsync(0, 8, false, cancellationToken);
        foreach (var isRequest in new[] { true, false })
        {
            var queue = Queue(local, isRequest);
            await consumer.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false, cancellationToken: cancellationToken);
            await consumer.QueueBindAsync(queue, Exchange, queue, cancellationToken: cancellationToken);
            await RabbitMqQuarantine.DeclareAsync(consumer, queue, cancellationToken);
            var handler = new AsyncEventingBasicConsumer(consumer);
            handler.ReceivedAsync += (_, delivery) => ReceiveAsync(delivery, isRequest);
            tags.Add(await consumer.BasicConsumeAsync(queue, false, handler, cancellationToken));
        }
    }

    public ValueTask PublishAsync(ProcessInvocationRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(request, request.Message, request.Identity.Target, true, cancellationToken);
    public ValueTask PublishAsync(ProcessInvocationAccepted accepted, CancellationToken cancellationToken = default) =>
        SendAsync(accepted, accepted.Message, accepted.Message.Identity.Source, false, cancellationToken);
    public ValueTask PublishAsync(ProcessInvocationRejected rejected, CancellationToken cancellationToken = default) =>
        SendAsync(rejected, rejected.Message, rejected.Message.Identity.Source, false, cancellationToken);
    public ValueTask PublishAsync(ProcessInvocationTerminalOutcome outcome, CancellationToken cancellationToken = default) =>
        SendAsync(outcome, outcome.Message, outcome.Message.Identity.Source, false, cancellationToken);

    public OutboxMessage PrepareOutbox(OutboxMessage message)
    {
        var invocation = message.Invocation ?? throw new InvalidOperationException("Invocation metadata is required.");
        var properties = message.ToInvocation() switch
        {
            ProcessInvocationRequest r => RabbitMqInvocationSerializer.Properties(r, r.Message),
            ProcessInvocationAccepted a => RabbitMqInvocationSerializer.Properties(a, a.Message),
            ProcessInvocationRejected r => RabbitMqInvocationSerializer.Properties(r, r.Message),
            ProcessInvocationTerminalOutcome t => RabbitMqInvocationSerializer.Properties(t, t.Message),
            _ => throw new InvalidOperationException("Unknown invocation message.")
        };
        return message with
        {
            Exchange = Exchange,
            Destination = Queue(invocation.Destination, invocation.IsRequest),
            ReplyDestination = properties.ReplyTo,
            WireProperties = new(properties.ContentType!, properties.ContentEncoding!, properties.CorrelationId!,
                properties.DeliveryMode == DeliveryModes.Persistent,
                properties.Headers!.ToDictionary(entry => entry.Key, entry => (string)entry.Value!, StringComparer.Ordinal))
        };
    }

    public ValueTask PublishOutboxAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        var invocation = message.Invocation ?? throw new InvalidOperationException("Invocation metadata is required.");
        var wire = message.WireProperties ?? throw new InvalidOperationException("Stored AMQP properties are required.");
        if (local != invocation.Publisher || message.Destination != Queue(invocation.Destination, invocation.IsRequest) ||
            message.Exchange != Exchange || message.ReplyDestination != (invocation.IsRequest ? Queue(invocation.Publisher, false) : null))
            throw new InvalidOperationException("Stored invocation route or local ownership is invalid.");
        var properties = new BasicProperties
        {
            Type = message.Name, MessageId = message.MessageId, ReplyTo = message.ReplyDestination,
            ContentType = wire.ContentType, ContentEncoding = wire.ContentEncoding, CorrelationId = wire.CorrelationId,
            DeliveryMode = wire.Persistent ? DeliveryModes.Persistent : DeliveryModes.Transient,
            Headers = wire.Headers.ToDictionary(entry => entry.Key, entry => (object?)entry.Value, StringComparer.Ordinal)
        };
        return SendWireAsync(message.Destination, properties, message.Body, cancellationToken);
    }

    private async ValueTask SendAsync<T>(T body, InvocationMessage message, ProcessIdentity destination, bool request, CancellationToken cancellationToken)
    {
        if (local != (request ? message.Identity.Source : message.Identity.Target)) throw new InvalidOperationException("Publisher does not own the message source/target process identity.");
        var queue = Queue(destination, request);
        if (queue.Length > 255) throw new InvalidOperationException("Invocation routing identity exceeds AMQP queue name limit.");
        await SendWireAsync(queue, RabbitMqInvocationSerializer.Properties(body, message),
            RabbitMqInvocationSerializer.Body(body), cancellationToken);
    }

    private async ValueTask SendWireAsync(string queue, BasicProperties properties, byte[] bytes, CancellationToken cancellationToken)
    {
        using var publication = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        await publishing.WaitAsync(publication.Token);
        try
        {
            var channel = publisher ?? throw new InvalidOperationException("Invocation transport is not started.");
            if (channel is { IsOpen: false, CloseReason: { Initiator: ShutdownInitiator.Peer, ReplyCode: 404 } })
            {
                var connection = await connections.GetConnectionAsync(publication.Token);
                publisher = await connection.CreateChannelAsync(new CreateChannelOptions(true, true), publication.Token);
                await channel.DisposeAsync();
                channel = publisher;
            }
            await channel.BasicPublishAsync(Exchange, queue, mandatory: true, properties, bytes, publication.Token);
        }
        catch (OperationCanceledException) when (publication.IsCancellationRequested) { throw; }
        catch (Exception error) { throw new IOException("Invocation publication failed or confirmation is uncertain.", error); }
        finally { publishing.Release(); }
    }

    private async Task ReceiveAsync(BasicDeliverEventArgs delivery, bool requestQueue)
    {
        var channel = consumer;
        if (channel is null || !channel.IsOpen || shutdown.IsCancellationRequested) return;
        try
        {
            if (!RabbitMqQuarantine.HasSupportedVersion(delivery.BasicProperties))
            {
                await QuarantineAsync(channel, Queue(local!, requestQueue), delivery);
                return;
            }
            await using var scope = scopes.CreateAsyncScope();
            var receiver = scope.ServiceProvider.GetRequiredService<ProcessInvocationReceiver>();
            var type = delivery.BasicProperties.Type;
            InvocationMessage message;
            Func<Task> receive;
            if (requestQueue && type == nameof(ProcessInvocationRequest))
            {
                var request = Decode<ProcessInvocationRequest>(delivery);
                message = request.Message;
                if (delivery.BasicProperties.ReplyTo != Queue(request.Identity.Source, false)) throw new ArgumentException("Invalid invocation reply routing.");
                receive = () => receiver.ReceiveAsync(request, shutdown.Token);
            }
            else if (!requestQueue && type == nameof(ProcessInvocationAccepted))
            {
                var accepted = Decode<ProcessInvocationAccepted>(delivery); message = accepted.Message;
                receive = () => receiver.ReceiveAsync(accepted, shutdown.Token);
            }
            else if (!requestQueue && type == nameof(ProcessInvocationRejected))
            {
                var rejected = Decode<ProcessInvocationRejected>(delivery); message = rejected.Message;
                receive = () => receiver.ReceiveAsync(rejected, shutdown.Token);
            }
            else if (!requestQueue && type == nameof(ProcessInvocationTerminalOutcome))
            {
                var outcome = Decode<ProcessInvocationTerminalOutcome>(delivery); message = outcome.Message;
                receive = () => receiver.ReceiveAsync(outcome, shutdown.Token);
            }
            else throw new ArgumentException("Unexpected invocation message type/queue.");
            if ((requestQueue ? message.Identity.Target : message.Identity.Source) != local
                || delivery.BasicProperties.MessageId != message.MessageId || delivery.BasicProperties.CorrelationId != message.Identity.CorrelationId)
                throw new ArgumentException("Invocation routing/metadata mismatch.");
            await receive();
            await channel.BasicAckAsync(delivery.DeliveryTag, false, shutdown.Token);
            Interlocked.Increment(ref handledMessages);
            if (type == nameof(ProcessInvocationAccepted))
            {
                // Acceptance is already committed/acked. A replay failure must not nack that delivery twice.
                try { await ReconcileDeferredAsync(cancellationToken: shutdown.Token); }
                catch (Exception error) { logger.LogWarning(error, "Deferred terminal reconciliation requires an explicit retry; parking queue is {Queue}.", DeferredQueue); }
            }
        }
        catch (InvocationOutcomeNotReadyException error)
        {
            // Confirm durable parking before acknowledging the original. Failure leaves the original retryable.
            try
            {
                await ParkAsync(delivery.Body, delivery.BasicProperties, shutdown.Token);
                await channel.BasicAckAsync(delivery.DeliveryTag, false, shutdown.Token);
                Interlocked.Increment(ref handledMessages);
                logger.LogWarning(error, "Terminal outcome {MessageId} parked in {Queue} awaiting acceptance/manual reconciliation.", delivery.BasicProperties.MessageId, DeferredQueue);
            }
            catch (Exception parkingFailure)
            {
                Interlocked.Increment(ref parkingFailures);
                logger.LogWarning(parkingFailure, "Parking failed; retaining terminal outcome {MessageId} for redelivery.", delivery.BasicProperties.MessageId);
                if (!shutdown.IsCancellationRequested) await Task.Delay(200, shutdown.Token);
                if (channel.IsOpen) await channel.BasicNackAsync(delivery.DeliveryTag, false, true, CancellationToken.None);
            }
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            logger.LogWarning(error, "Rejecting invalid/conflicting invocation message {MessageId} without requeue.", delivery.BasicProperties.MessageId);
            await channel.BasicRejectAsync(delivery.DeliveryTag, false, CancellationToken.None);
            Interlocked.Increment(ref rejectedMessages);
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "Invocation message {MessageId} will be redelivered; admission/identity is retained.", delivery.BasicProperties.MessageId);
            if (!shutdown.IsCancellationRequested) await Task.Delay(200, shutdown.Token);
            if (channel.IsOpen) await channel.BasicNackAsync(delivery.DeliveryTag, false, true, CancellationToken.None);
        }
    }
    private async Task ParkAsync(ReadOnlyMemory<byte> body, IReadOnlyBasicProperties metadata, CancellationToken cancellationToken)
    {
        var properties = new BasicProperties(metadata) { DeliveryMode = DeliveryModes.Persistent };
        await publishing.WaitAsync(cancellationToken);
        try { await (publisher ?? throw new InvalidOperationException("Publisher is closed.")).BasicPublishAsync("", DeferredQueue, true, properties, body, cancellationToken); }
        finally { publishing.Release(); }
    }

    public async ValueTask<uint> GetDeferredCountAsync(CancellationToken cancellationToken = default)
    {
        await reconciling.WaitAsync(cancellationToken);
        try { return await (parking ?? throw new InvalidOperationException("Parking channel is closed.")).MessageCountAsync(DeferredQueue, cancellationToken); }
        finally { reconciling.Release(); }
    }

    public async ValueTask<int> ReconcileDeferredAsync(int maxMessages = 64, CancellationToken cancellationToken = default)
    {
        if (maxMessages is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(maxMessages));
        await reconciling.WaitAsync(cancellationToken);
        try
        {
            var channel = parking ?? throw new InvalidOperationException("Parking channel is closed.");
            var count = Math.Min((uint)maxMessages, await channel.MessageCountAsync(DeferredQueue, cancellationToken));
            var applied = 0;
            for (var i = 0u; i < count; i++)
            {
                var delivery = await channel.BasicGetAsync(DeferredQueue, false, cancellationToken);
                if (delivery is null) break;
                Interlocked.Increment(ref deferredReplays);
                if (!RabbitMqQuarantine.HasSupportedVersion(delivery.BasicProperties))
                {
                    try
                    {
                        await publishing.WaitAsync(cancellationToken);
                        try { await RabbitMqQuarantine.TransferAsync(publisher!, DeferredQueue, delivery.Exchange, delivery.RoutingKey,
                            delivery.BasicProperties, delivery.Body, cancellationToken); }
                        finally { publishing.Release(); }
                        await channel.BasicAckAsync(delivery.DeliveryTag, false, cancellationToken);
                    }
                    catch (Exception transferError)
                    {
                        // This branch is outside the terminal-processing catch blocks: disposition happens once.
                        if (channel.IsOpen)
                        {
                            try { await channel.BasicNackAsync(delivery.DeliveryTag, false, true, CancellationToken.None); }
                            catch (Exception nackError)
                            {
                                logger.LogCritical(nackError, "Legacy parking requeue failed; source delivery/ACK state is uncertain. Inspect channel and quarantine copies before retry.");
                                throw new AggregateException("Legacy quarantine transfer and source requeue failed.", transferError, nackError);
                            }
                        }
                        else logger.LogWarning(transferError, "Legacy parking channel closed; source requeue/ACK is uncertain. Inspect broker state before retry.");
                        throw;
                    }
                    continue;
                }
                try
                {
                    if (delivery.BasicProperties.Type != nameof(ProcessInvocationTerminalOutcome)) throw new ArgumentException("Parking queue contains a non-terminal message.");
                    var outcome = RabbitMqInvocationSerializer.Read<ProcessInvocationTerminalOutcome>(delivery.Body.Span, delivery.BasicProperties);
                    if (outcome.Message.Identity.Source != local || delivery.BasicProperties.MessageId != outcome.Message.MessageId
                        || delivery.BasicProperties.CorrelationId != outcome.Message.Identity.CorrelationId) throw new ArgumentException("Parked terminal metadata/identity mismatch.");
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<ProcessInvocationReceiver>().ReceiveAsync(outcome, cancellationToken);
                    await channel.BasicAckAsync(delivery.DeliveryTag, false, cancellationToken);
                    applied++;
                }
                catch (Exception waiting) when (waiting is InvocationOutcomeNotReadyException or KeyNotFoundException)
                {
                    logger.LogWarning(waiting, "Keeping terminal {MessageId} parked for acceptance/source-state reconciliation.", delivery.BasicProperties.MessageId);
                    try
                    {
                        // Rotate to the tail, retaining the original ID. A pass is capped at the initial ready count.
                        await ParkAsync(delivery.Body, delivery.BasicProperties, cancellationToken);
                        await channel.BasicAckAsync(delivery.DeliveryTag, false, cancellationToken);
                    }
                    catch { if (channel.IsOpen) await channel.BasicNackAsync(delivery.DeliveryTag, false, true, CancellationToken.None); throw; }
                }
                catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
                {
                    logger.LogWarning(error, "Rejecting invalid/conflicting parked terminal {MessageId}.", delivery.BasicProperties.MessageId);
                    await channel.BasicRejectAsync(delivery.DeliveryTag, false, CancellationToken.None);
                }
                catch
                {
                    if (channel.IsOpen) await channel.BasicNackAsync(delivery.DeliveryTag, false, true, CancellationToken.None);
                    throw;
                }
            }
            return applied;
        }
        finally { reconciling.Release(); }
    }

    private static T Decode<T>(BasicDeliverEventArgs delivery) => RabbitMqInvocationSerializer.Read<T>(delivery.Body.Span, delivery.BasicProperties);

    private async Task QuarantineAsync(IChannel source, string queue, BasicDeliverEventArgs delivery)
    {
        try
        {
            await publishing.WaitAsync(shutdown.Token);
            try { await RabbitMqQuarantine.TransferAsync(publisher!, queue, delivery, shutdown.Token); }
            finally { publishing.Release(); }
            await source.BasicAckAsync(delivery.DeliveryTag, false, shutdown.Token);
            Interlocked.Increment(ref handledMessages);
        }
        catch (Exception error)
        {
            shutdown.Cancel();
            logger.LogCritical(error, "Invocation quarantine transfer/ACK failed; stopping consumption. Restore quarantine and restart host.");
            quarantineShutdown = Task.Run(() => StopAsync(CancellationToken.None));
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await stopping.WaitAsync(cancellationToken);
        try
        {
            shutdown.Cancel();
            if (consumer is { } active)
            {
                foreach (var tag in tags) await RabbitMqTransport.CancelConsumerAsync(active, tag, cancellationToken);
                tags.Clear();
                if (active.IsOpen) await active.CloseAsync(CancellationToken.None);
                await active.DisposeAsync(); consumer = null;
            }
            await reconciling.WaitAsync(CancellationToken.None);
            try
            {
                if (parking is { } deferred)
                {
                    if (deferred.IsOpen) await deferred.CloseAsync(CancellationToken.None);
                    await deferred.DisposeAsync(); parking = null;
                }
            }
            finally { reconciling.Release(); }
            await publishing.WaitAsync(CancellationToken.None);
            try
            {
                if (publisher is { } activePublisher)
                {
                    if (activePublisher.IsOpen) await activePublisher.CloseAsync(CancellationToken.None);
                    await activePublisher.DisposeAsync(); publisher = null;
                }
            }
            finally { publishing.Release(); }
        }
        finally { stopping.Release(); }
    }
    public async ValueTask DisposeAsync() { await StopAsync(CancellationToken.None); if (quarantineShutdown is { } task) await task; }
}
