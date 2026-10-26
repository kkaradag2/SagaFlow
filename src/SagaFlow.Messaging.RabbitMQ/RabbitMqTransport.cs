using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Messages;
using SagaFlow.Engine.Coordination;
using SagaFlow.Engine.Messaging;

namespace SagaFlow.Messaging.RabbitMQ;

internal sealed class RabbitMqTransport(
    RabbitMqConnectionProvider connections,
    RabbitMqOptions options,
    IServiceScopeFactory scopeFactory,
    ILogger<RabbitMqTransport> logger) : ISagaMessageBus, IHostedService, IAsyncDisposable
{
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private readonly CancellationTokenSource _consumerCancellation = new();
    private IChannel? _publisherChannel;
    private IChannel? _consumerChannel;
    private string? _consumerTag;
    private bool _disposed;

    internal IChannel? PublisherChannel => _publisherChannel;
    internal IChannel? ConsumerChannel => _consumerChannel;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var connection = await connections.GetConnectionAsync(cancellationToken);
        _publisherChannel = await connection.CreateChannelAsync(
            new CreateChannelOptions(true, true), cancellationToken);
        _consumerChannel = await connection.CreateChannelAsync(
            new CreateChannelOptions(false, false, consumerDispatchConcurrency: 1),
            cancellationToken);

        await DeclareTopologyAsync(_consumerChannel, cancellationToken);
        await _consumerChannel.BasicQosAsync(0, options.PrefetchCount, false, cancellationToken);

        var consumer = new AsyncEventingBasicConsumer(_consumerChannel);
        consumer.ReceivedAsync += HandleOutcomeAsync;
        _consumerTag = await _consumerChannel.BasicConsumeAsync(
            options.OutcomeQueue, autoAck: false, consumer, cancellationToken);
    }

    public async ValueTask PublishAsync(SagaCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Name);
        ArgumentNullException.ThrowIfNull(command.Headers);
        if (_publisherChannel is not { IsOpen: true } channel)
            throw new InvalidOperationException("RabbitMQ transport has not started or its publisher channel is closed.");

        var body = RabbitMqMessageSerializer.Serialize(command);
        var properties = new BasicProperties
        {
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            Type = nameof(SagaCommand),
            MessageId = $"{command.Headers.SagaInstanceId}:{command.Headers.StepId}",
            CorrelationId = command.Headers.CorrelationId,
            ReplyTo = options.OutcomeQueue
        };

        await _publishGate.WaitAsync(cancellationToken);
        try
        {
            await channel.BasicPublishAsync(
                options.CommandExchange,
                command.Name,
                mandatory: true,
                basicProperties: properties,
                body,
                cancellationToken);
        }
        finally
        {
            _publishGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _consumerCancellation.Cancel();
        if (_consumerTag is not null && _consumerChannel is { IsOpen: true })
        {
            try { await _consumerChannel.BasicCancelAsync(_consumerTag, cancellationToken: cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }

        await CloseChannelAsync(_consumerChannel);
        await CloseChannelAsync(_publisherChannel);
        _consumerChannel = null;
        _publisherChannel = null;
        _consumerTag = null;
        await connections.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync(CancellationToken.None);
        _publishGate.Dispose();
        _consumerCancellation.Dispose();
    }

    private async Task DeclareTopologyAsync(IChannel channel, CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(
            options.CommandExchange,
            ExchangeType.Direct,
            durable: true,
            autoDelete: false,
            cancellationToken: cancellationToken);

        foreach (var route in options.CommandRoutes)
        {
            await channel.QueueDeclareAsync(
                route.Value,
                durable: true,
                exclusive: false,
                autoDelete: false,
                cancellationToken: cancellationToken);
            await channel.QueueBindAsync(
                route.Value,
                options.CommandExchange,
                route.Key,
                cancellationToken: cancellationToken);
        }

        await channel.QueueDeclareAsync(
            options.OutcomeQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            cancellationToken: cancellationToken);
    }

    private async Task HandleOutcomeAsync(object sender, BasicDeliverEventArgs delivery)
    {
        var channel = _consumerChannel;
        if (channel is null || !channel.IsOpen) return;

        try
        {
            var outcome = RabbitMqMessageSerializer.DeserializeOutcome(delivery.Body.Span);
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<OutcomeEventReceiver>()
                .ReceiveAsync(outcome, _consumerCancellation.Token);
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, _consumerCancellation.Token);
        }
        catch (JsonException error)
        {
            logger.LogError(error, "Discarding malformed Saga outcome message {MessageId}.", delivery.BasicProperties.MessageId);
            await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, CancellationToken.None);
        }
        catch (DuplicateSagaOutcomeException error)
        {
            logger.LogInformation(error, "Acknowledging duplicate Saga outcome {MessageId}.", delivery.BasicProperties.MessageId);
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, CancellationToken.None);
        }
        catch (InvalidSagaOutcomeException error)
        {
            logger.LogWarning(error, "Rejecting invalid Saga outcome {MessageId}.", delivery.BasicProperties.MessageId);
            await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, CancellationToken.None);
        }
        catch (SagaDispatchException error)
        {
            logger.LogError(error, "Saga outcome was applied, but the next command could not be published. Acknowledging outcome {MessageId}.", delivery.BasicProperties.MessageId);
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, CancellationToken.None);
        }
        catch (OperationCanceledException) when (_consumerCancellation.IsCancellationRequested)
        {
            // Closing the channel requeues the unacknowledged delivery.
        }
        catch (Exception error)
        {
            logger.LogError(error, "Saga outcome {MessageId} failed and will be requeued.", delivery.BasicProperties.MessageId);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, CancellationToken.None);
        }
    }

    private static async ValueTask CloseChannelAsync(IChannel? channel)
    {
        if (channel is null) return;
        if (channel.IsOpen) await channel.CloseAsync(CancellationToken.None);
        await channel.DisposeAsync();
    }
}
