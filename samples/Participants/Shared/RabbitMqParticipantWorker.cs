using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using SagaFlow.Core.Messages;

namespace SagaFlow.Participants.Messaging;

public interface IParticipantCommandHandler
{
    IReadOnlyCollection<string> CommandNames { get; }
    ParticipantCommandResult Handle(SagaCommand command);
}

public sealed record ParticipantCommandResult(bool IsSuccess, object Value)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static ParticipantCommandResult Success(object value) => new(true, value);
    public static ParticipantCommandResult Failure(string participant) =>
        new(false, new { error = $"Sample failure requested at {participant}." });

    public SagaOutcomeEvent ToOutcome(SagaCommand command) => new()
    {
        Name = $"{command.Name}{(IsSuccess ? "Completed" : "Failed")}",
        IsSuccess = IsSuccess,
        Headers = command.Headers,
        Result = JsonSerializer.SerializeToElement(Value, JsonOptions)
    };
}

public static class SampleFailureInjection
{
    public static bool IsRequested(SagaCommand command, string participant, string forwardCommand) =>
        command.Name == forwardCommand
        && command.Payload.TryGetProperty("failureAt", out var failureAt)
        && failureAt.ValueKind == JsonValueKind.String
        && string.Equals(failureAt.GetString(), participant, StringComparison.Ordinal);
}

public class RabbitMqParticipantWorker(
    IParticipantCommandHandler handler,
    IConfiguration configuration,
    ILogger<RabbitMqParticipantWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = configuration.GetRequiredSection("RabbitMq");
        var queueName = settings["QueueName"]
            ?? throw new InvalidOperationException("RabbitMq:QueueName is required.");
        var exchange = settings["CommandExchange"] ?? "sagaflow.commands";
        var factory = new ConnectionFactory
        {
            HostName = settings["HostName"] ?? "localhost",
            Port = settings.GetValue<int?>("Port") ?? AmqpTcpEndpoint.UseDefaultPort,
            UserName = settings["UserName"] ?? ConnectionFactory.DefaultUser,
            Password = settings["Password"] ?? ConnectionFactory.DefaultPass,
            VirtualHost = settings["VirtualHost"] ?? ConnectionFactory.DefaultVHost,
            ClientProvidedName = settings["ClientProvidedName"] ?? queueName,
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true
        };

        await using var connection = await factory.CreateConnectionAsync(stoppingToken);
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(true, true, consumerDispatchConcurrency: 1), stoppingToken);
        await DeclareTopologyAsync(channel, exchange, queueName, stoppingToken);
        await channel.BasicQosAsync(0, 1, false, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, delivery) => HandleDeliveryAsync(channel, delivery, stoppingToken);
        await channel.BasicConsumeAsync(queueName, autoAck: false, consumer, stoppingToken);

        logger.LogInformation("Listening for {Commands} on {Queue}.", string.Join(", ", handler.CommandNames), queueName);
        try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task DeclareTopologyAsync(
        IChannel channel,
        string exchange,
        string queueName,
        CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(exchange, ExchangeType.Direct, durable: true, autoDelete: false,
            cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(queueName, durable: true, exclusive: false, autoDelete: false,
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(queueName, exchange, queueName, cancellationToken: cancellationToken);
    }

    private async Task HandleDeliveryAsync(
        IChannel channel,
        BasicDeliverEventArgs delivery,
        CancellationToken cancellationToken)
    {
        SagaCommand command;
        try
        {
            command = JsonSerializer.Deserialize<SagaCommand>(delivery.Body.Span, JsonOptions)
                ?? throw new JsonException("Saga command payload is null.");
            if (command.Headers is null ||
                string.IsNullOrWhiteSpace(command.Headers.SagaInstanceId) ||
                string.IsNullOrWhiteSpace(command.Headers.CorrelationId) ||
                string.IsNullOrWhiteSpace(command.Headers.StepId) ||
                !handler.CommandNames.Contains(command.Name, StringComparer.Ordinal))
                throw new JsonException("Saga command name and correlation headers are invalid.");
            if (string.IsNullOrWhiteSpace(delivery.BasicProperties.ReplyTo))
                throw new JsonException("Saga command ReplyTo is required.");
        }
        catch (JsonException error)
        {
            logger.LogWarning(error, "Rejecting malformed participant command {MessageId}.", delivery.BasicProperties.MessageId);
            await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, CancellationToken.None);
            return;
        }

        var outcome = CreateOutcome(command);
        try
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(outcome, JsonOptions);
            await channel.BasicPublishAsync(
                exchange: string.Empty,
                routingKey: delivery.BasicProperties.ReplyTo!,
                mandatory: true,
                basicProperties: new BasicProperties
                {
                    ContentType = "application/json",
                    DeliveryMode = DeliveryModes.Persistent,
                    Type = nameof(SagaOutcomeEvent),
                    MessageId = $"{command.Headers.SagaInstanceId}:{command.Headers.StepId}:outcome",
                    CorrelationId = command.Headers.CorrelationId
                },
                body,
                cancellationToken);
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            logger.LogError(error, "Outcome publication failed for command {Command}; requeueing.", command.Name);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, CancellationToken.None);
        }
    }

    private SagaOutcomeEvent CreateOutcome(SagaCommand command)
    {
        try
        {
            return handler.Handle(command).ToOutcome(command);
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "Command {Command} failed in participant domain service.", command.Name);
            return new()
            {
                Name = $"{command.Name}Failed",
                IsSuccess = false,
                Headers = command.Headers,
                Result = JsonSerializer.SerializeToElement(new { error = error.Message }, JsonOptions)
            };
        }
    }
}
