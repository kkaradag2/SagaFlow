using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using SagaFlow.Participants.Messaging;

namespace Sample.Participants.Messaging;

internal sealed class RabbitMqParticipantWorker(
    ParticipantRabbitMqSettings settings, IParticipantCommandHandler handler, ILogger<RabbitMqParticipantWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = new ConnectionFactory
        {
            Uri = settings.ConnectionUri,
            ClientProvidedName = settings.ClientProvidedName,
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true
        };

        await using var connection = await factory.CreateConnectionAsync(stoppingToken);
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(true, true, consumerDispatchConcurrency: 1), stoppingToken);
        await channel.ExchangeDeclareAsync(settings.CommandExchange, ExchangeType.Direct, durable: true,
            autoDelete: false, cancellationToken: stoppingToken);
        await channel.QueueDeclareAsync(settings.QueueName, durable: true, exclusive: false, autoDelete: false,
            cancellationToken: stoppingToken);
        await channel.QueueBindAsync(settings.QueueName, settings.CommandExchange, settings.RoutingKey,
            cancellationToken: stoppingToken);
        await channel.BasicQosAsync(0, 1, false, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, delivery) => HandleAsync(channel, delivery, stoppingToken);
        await channel.BasicConsumeAsync(settings.QueueName, autoAck: false, consumer, stoppingToken);

        logger.LogInformation("{Participant} listening on {Queue} ({Exchange}/{RoutingKey})",
            settings.ClientProvidedName, settings.QueueName, settings.CommandExchange, settings.RoutingKey);
        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }

    private async Task HandleAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken cancellationToken)
    {
        try
        {
            var replyTo = delivery.BasicProperties.ReplyTo;
            if (replyTo is null || !settings.OutcomeQueues.Contains(replyTo))
                throw new JsonException("Participant command has an unconfigured ReplyTo queue.");

            var command = ParticipantProtocol.ReadCommand(delivery.Body.Span, delivery.BasicProperties);
            var outcome = await handler.HandleAsync(command, cancellationToken);
            await channel.BasicPublishAsync(exchange: string.Empty, routingKey: replyTo, mandatory: true,
                basicProperties: ParticipantProtocol.OutcomeProperties(outcome),
                body: JsonSerializer.SerializeToUtf8Bytes(outcome.Result), cancellationToken: cancellationToken);
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken);
        }
        catch (JsonException error)
        {
            logger.LogWarning(error, "Rejecting invalid participant message {MessageId}", delivery.BasicProperties.MessageId);
            await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            logger.LogError(error, "Participant message {MessageId} will be retried", delivery.BasicProperties.MessageId);
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            if (channel.IsOpen)
                await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, cancellationToken);
        }
    }
}
