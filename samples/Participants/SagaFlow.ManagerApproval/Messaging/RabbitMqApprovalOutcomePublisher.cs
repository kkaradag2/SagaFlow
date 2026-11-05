using System.Text.Json;
using RabbitMQ.Client;
using SagaFlow.Participants.Messaging;

namespace SagaFlow.ManagerApproval.Messaging;

public interface IApprovalOutcomePublisher
{
    /// <summary>Throw ApprovalOutcomeNotSentException only when BasicPublishAsync was never invoked.</summary>
    Task PublishAsync(Guid approvalId, bool approved, CancellationToken cancellationToken);
}

public sealed class ApprovalOutcomeNotSentException(Exception innerException)
    : Exception($"Approval outcome publication did not start: {innerException.Message}", innerException);

public sealed class RabbitMqApprovalOutcomePublisher(IConfiguration configuration, DeferredReplies replies) : IApprovalOutcomePublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task PublishAsync(Guid approvalId, bool approved, CancellationToken cancellationToken)
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
            var publication = replies.Prepare(replies.ForBusinessRecord(approvalId), "RequestManagerApprovalCompleted", new { approved });
            var outcome = publication.Outcome;
            var replyTo = publication.ReplyTo;
            var body = JsonSerializer.SerializeToUtf8Bytes(outcome.Result, JsonOptions);
            var properties = ParticipantProtocol.OutcomeProperties(outcome);
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
