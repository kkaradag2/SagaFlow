using System.Text.Json;
using RabbitMQ.Client;
using SagaFlow.Core.Messages;

namespace SagaFlow.ManagerApproval.Messaging;

public interface IApprovalOutcomePublisher
{
    /// <summary>Throw ApprovalOutcomeNotSentException only when BasicPublishAsync was never invoked.</summary>
    Task PublishAsync(SagaOutcomeEvent outcome, string replyTo, CancellationToken cancellationToken);
}

public sealed class ApprovalOutcomeNotSentException(Exception innerException)
    : Exception($"Approval outcome publication did not start: {innerException.Message}", innerException);

public sealed class RabbitMqApprovalOutcomePublisher(IConfiguration configuration) : IApprovalOutcomePublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task PublishAsync(SagaOutcomeEvent outcome, string replyTo, CancellationToken cancellationToken)
    {
        var publishStarted = false;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var settings = configuration.GetRequiredSection("RabbitMq");
            var factory = new ConnectionFactory
            {
                HostName = settings["HostName"] ?? "localhost",
                Port = settings.GetValue<int?>("Port") ?? AmqpTcpEndpoint.UseDefaultPort,
                UserName = settings["UserName"] ?? ConnectionFactory.DefaultUser,
                Password = settings["Password"] ?? ConnectionFactory.DefaultPass,
                VirtualHost = settings["VirtualHost"] ?? ConnectionFactory.DefaultVHost,
                ClientProvidedName = "sagaflow-manager-decision", AutomaticRecoveryEnabled = false
            };
            await using var connection = await factory.CreateConnectionAsync(deadline.Token);
            await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true), deadline.Token);
            var body = JsonSerializer.SerializeToUtf8Bytes(outcome, JsonOptions);
            var properties = new BasicProperties
            {
                ContentType = "application/json", DeliveryMode = DeliveryModes.Persistent,
                Type = nameof(SagaOutcomeEvent),
                MessageId = $"{outcome.Headers.SagaInstanceId}:{outcome.Headers.StepId}:outcome",
                CorrelationId = outcome.Headers.CorrelationId
            };
            // Mark the boundary immediately before the RabbitMQ call, after all local setup.
            // Once entered, even a cancellation or cleanup failure must conservatively prevent retry.
            publishStarted = true;
            await channel.BasicPublishAsync(exchange: string.Empty, routingKey: replyTo, mandatory: true,
                basicProperties: properties, body: body, cancellationToken: deadline.Token);
        }
        catch (Exception error) when (!publishStarted)
        {
            throw new ApprovalOutcomeNotSentException(error);
        }
    }
}
