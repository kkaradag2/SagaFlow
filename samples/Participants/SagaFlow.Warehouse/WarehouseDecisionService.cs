using System.Text.Json;
using RabbitMQ.Client;
using SagaFlow.Participants.Messaging;

namespace SagaFlow.Warehouse;

public sealed class WarehouseOutcomeNotSentException(Exception inner)
    : Exception("Warehouse outcome publication did not start.", inner);

public interface IWarehouseOutcomePublisher
{
    Task PublishAsync(Guid inspectionId, bool accepted);
}

public sealed class WarehouseOutcomePublisher(IConfiguration configuration, DeferredReplies replies) : IWarehouseOutcomePublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public async Task PublishAsync(Guid inspectionId, bool accepted)
    {
        var publishStarted = false;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var settings = configuration.GetRequiredSection("RabbitMq");
            var factory = new ConnectionFactory {
                HostName = settings["HostName"] ?? "localhost",
                Port = settings.GetValue<int?>("Port") ?? AmqpTcpEndpoint.UseDefaultPort,
                UserName = settings["UserName"] ?? ConnectionFactory.DefaultUser,
                Password = settings["Password"] ?? ConnectionFactory.DefaultPass,
                VirtualHost = settings["VirtualHost"] ?? ConnectionFactory.DefaultVHost,
                ClientProvidedName = "sagaflow-warehouse-decision", AutomaticRecoveryEnabled = false
            };
            await using var connection = await factory.CreateConnectionAsync(deadline.Token);
            await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true), deadline.Token);
            var publication = replies.Prepare(replies.ForBusinessRecord(inspectionId), "InspectReturnCompleted", new { accepted });
            var outcome = publication.Outcome;
            var replyTo = publication.ReplyTo;
            var properties = ParticipantProtocol.OutcomeProperties(outcome);
            publishStarted = true;
            await channel.BasicPublishAsync(exchange: string.Empty, routingKey: replyTo, mandatory: true,
                basicProperties: properties, body: JsonSerializer.SerializeToUtf8Bytes(outcome.Result, JsonOptions),
                cancellationToken: deadline.Token);
        }
        catch (Exception error) when (!publishStarted) { throw new WarehouseOutcomeNotSentException(error); }
    }
}

public sealed record WarehouseDecisionResult(bool Published, InspectionRecord Inspection);

public sealed class WarehouseDecisionService(WarehouseRepository repository, IWarehouseOutcomePublisher publisher,
    TimeProvider timeProvider, ILogger<WarehouseDecisionService> logger)
{
    public async Task<WarehouseDecisionResult> DecideAsync(Guid returnId, bool accepted, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var claim = repository.BeginDecision(returnId, accepted, timeProvider.GetUtcNow());
        try { await publisher.PublishAsync(claim.InspectionId, accepted); }
        catch (WarehouseOutcomeNotSentException error)
        {
            logger.LogError(error, "Warehouse outcome not sent for return {ReturnId}", returnId);
            return new(false, repository.Complete(claim, error.Message, true, timeProvider.GetUtcNow()));
        }
        catch (Exception error)
        {
            logger.LogError(error, "Warehouse outcome publication uncertain for return {ReturnId}", returnId);
            return new(false, repository.Complete(claim, error.Message, false, timeProvider.GetUtcNow()));
        }
        return new(true, repository.Complete(claim, null, false, timeProvider.GetUtcNow()));
    }
}
