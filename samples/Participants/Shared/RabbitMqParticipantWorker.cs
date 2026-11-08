using SagaFlow.RabbitMq.Protocol;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace SagaFlow.Participants.Messaging;

public interface IParticipantCommandHandler
{
    IReadOnlyCollection<string> CommandNames { get; }
    // Null means the command was accepted for a deferred outcome (for example a human decision).
    ParticipantCommandResult? Handle(ParticipantCommand command, string? replyTo = null);
}

public sealed record ParticipantCommandResult(bool IsSuccess, object Value)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static ParticipantCommandResult Success(object value) => new(true, value);
    public static ParticipantCommandResult Failure(string participant) =>
        new(false, new { error = $"Sample failure requested at {participant}." });

    public ParticipantOutcome ToOutcome(ParticipantCommand command) => new()
    {
        Name = $"{command.Name}{(IsSuccess ? "Completed" : "Failed")}",
        IsSuccess = IsSuccess,
        Headers = command.Headers,
        Result = JsonSerializer.SerializeToElement(Value, JsonOptions)
    };
}

public static class SampleFailureInjection
{
    public static bool IsRequested(ParticipantCommand command, string participant, string forwardCommand) =>
        command.Name == forwardCommand
        && string.Equals(command.Bind<SampleFailureRequest>().FailureAt, participant, StringComparison.Ordinal);
}

public class RabbitMqParticipantWorker(
    IParticipantCommandHandler handler,
    IConfiguration configuration,
    ILogger<RabbitMqParticipantWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TaskCompletionSource _quarantineFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = configuration.GetRequiredSection("RabbitMq");
        var queueNames = settings.GetSection("QueueNames").Get<string[]>()
            ?? [settings["QueueName"] ?? throw new InvalidOperationException("RabbitMq:QueueName is required.")];
        if (queueNames.Length == 0 || queueNames.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("RabbitMq queue names must not be empty.");
        queueNames = queueNames.Distinct(StringComparer.Ordinal).ToArray();
        var exchange = settings["CommandExchange"] ?? "sagaflow.commands";
        var factory = new ConnectionFactory
        {
            HostName = settings["HostName"] ?? "localhost",
            Port = settings.GetValue<int?>("Port") ?? AmqpTcpEndpoint.UseDefaultPort,
            UserName = settings["UserName"] ?? ConnectionFactory.DefaultUser,
            Password = settings["Password"] ?? ConnectionFactory.DefaultPass,
            VirtualHost = settings["VirtualHost"] ?? ConnectionFactory.DefaultVHost,
            ClientProvidedName = settings["ClientProvidedName"] ?? queueNames[0],
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true
        };

        await using var connection = await factory.CreateConnectionAsync(stoppingToken);
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(true, true, consumerDispatchConcurrency: 1), stoppingToken);
        foreach (var queueName in queueNames)
            await DeclareTopologyAsync(channel, exchange, queueName, stoppingToken);
        await channel.BasicQosAsync(0, 1, false, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, delivery) => HandleDeliveryAsync(channel, delivery, stoppingToken);
        foreach (var queueName in queueNames)
            await channel.BasicConsumeAsync(queueName, autoAck: false, consumer, stoppingToken);

        logger.LogInformation("Listening for {Commands} on {Queues}.", string.Join(", ", handler.CommandNames), string.Join(", ", queueNames));
        // Dispose/close outside the consumer callback, after the failure signal unwinds that callback.
        try { await Task.WhenAny(Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken), _quarantineFailure.Task); }
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
        await RabbitMqQuarantine.DeclareAsync(channel, queueName, cancellationToken);
    }

    private async Task HandleDeliveryAsync(
        IChannel channel,
        BasicDeliverEventArgs delivery,
        CancellationToken cancellationToken)
    {
        if (!channel.IsOpen || cancellationToken.IsCancellationRequested || _quarantineFailure.Task.IsCompleted) return;
        if (!RabbitMqQuarantine.HasSupportedVersion(delivery.BasicProperties))
        {
            try
            {
                await RabbitMqQuarantine.TransferAsync(channel, delivery.RoutingKey, delivery, cancellationToken);
                await channel.BasicAckAsync(delivery.DeliveryTag, false, cancellationToken);
                logger.LogWarning("Preserved incompatible participant command {MessageId} in {Queue}.",
                    delivery.BasicProperties.MessageId, RabbitMqQuarantine.Queue(delivery.RoutingKey));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception error)
            {
                logger.LogCritical(error, "Command quarantine transfer/ACK failed. Stopping this worker's consumption; restore quarantine and restart the worker. Unacked source deliveries are retained; duplicate copies are possible.");
                // Wake ExecuteAsync so its channel scope closes outside the dispatcher callback.
                _quarantineFailure.TrySetResult();
            }
            return;
        }
        ParticipantCommand command;
        try
        {
            command = ParticipantProtocol.ReadCommand(delivery.Body.Span, delivery.BasicProperties);
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

        var outcome = CreateOutcome(command, delivery.BasicProperties.ReplyTo!);
        try
        {
            if (outcome is not null)
                await ParticipantOutcomePublisher.PublishAsync(channel, outcome, delivery.BasicProperties.ReplyTo!, cancellationToken);
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            logger.LogError(error, "Outcome publication failed for command {Command}; requeueing.", command.Name);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, CancellationToken.None);
        }
    }

    private ParticipantOutcome? CreateOutcome(ParticipantCommand command, string replyTo)
    {
        try
        {
            return handler.Handle(command, replyTo)?.ToOutcome(command);
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

public static class ParticipantOutcomePublisher
{
    public static ValueTask PublishAsync(IChannel channel, ParticipantOutcome outcome, string replyTo, CancellationToken cancellationToken) =>
        channel.BasicPublishAsync(exchange: string.Empty, routingKey: replyTo, mandatory: true,
            basicProperties: ParticipantProtocol.OutcomeProperties(outcome),
            body: JsonSerializer.SerializeToUtf8Bytes(outcome.Result), cancellationToken: cancellationToken);
}
