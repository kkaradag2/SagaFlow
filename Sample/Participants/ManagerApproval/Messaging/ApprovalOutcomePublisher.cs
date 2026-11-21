using System.Text.Json;
using RabbitMQ.Client;
using SagaFlow.Participants.Messaging;
using Sample.Participants.Messaging;

namespace Sample.Participants.ManagerApproval.Messaging;

internal sealed record ApprovalPublication(
    Guid ApprovalId, Guid MessageId, bool Approved, string ReplyTo, string CorrelationId, string HeadersJson);

internal sealed class ApprovalOutcomePublisher(ParticipantRabbitMqSettings settings)
{
    public async Task PublishAsync(ApprovalPublication publication, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var headers = JsonSerializer.Deserialize<Dictionary<string, byte[]>>(publication.HeadersJson)
            ?? throw new JsonException("Approval outcome headers are missing.");
        var outcome = new ParticipantOutcome
        {
            MessageId = publication.MessageId.ToString("N"),
            Name = "RequestManagerApprovalCompleted",
            IsSuccess = true,
            Result = JsonSerializer.SerializeToElement(new { approved = publication.Approved }),
            Headers = new ParticipantContext
            {
                CorrelationId = publication.CorrelationId,
                Headers = headers.ToDictionary(entry => entry.Key, entry => (object?)entry.Value)
            }
        };

        var factory = new ConnectionFactory
        {
            Uri = settings.ConnectionUri,
            ClientProvidedName = settings.ClientProvidedName + "-outbox",
            AutomaticRecoveryEnabled = false
        };
        await using var connection = await factory.CreateConnectionAsync(deadline.Token);
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true), deadline.Token);
        await channel.BasicPublishAsync(exchange: string.Empty, routingKey: publication.ReplyTo, mandatory: true,
            basicProperties: ParticipantProtocol.OutcomeProperties(outcome),
            body: JsonSerializer.SerializeToUtf8Bytes(outcome.Result), cancellationToken: deadline.Token);
    }
}
