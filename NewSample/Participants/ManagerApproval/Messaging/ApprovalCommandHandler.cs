using System.Text;
using System.Text.Json;
using SagaFlow.Participants.Messaging;
using Sample.Participants.ManagerApproval.Data;
using Sample.Participants.Messaging;

namespace Sample.Participants.ManagerApproval.Messaging;

internal sealed class ApprovalCommandHandler(ApprovalRepository approvals) : IParticipantCommandHandler
{
    public async Task<ParticipantOutcome?> HandleAsync(
        ParticipantCommand command, string replyTo, CancellationToken cancellationToken)
    {
        if (command.Name != "RequestManagerApproval" || command.Headers.IsCompensation)
            throw new JsonException("Only forward RequestManagerApproval commands are supported.");

        var request = command.Bind<ApprovalRequest>();
        if (request.Id == Guid.Empty || request.TotalQuantity <= 0)
            throw new JsonException("Manager approval requires an order ID and a positive total quantity.");

        // JSON stores byte arrays as base64, preserving the original execution snapshot.
        var headers = command.Headers.Headers.ToDictionary(pair => pair.Key, pair => pair.Value switch
        {
            byte[] bytes => bytes,
            string value => Encoding.UTF8.GetBytes(value),
            _ => throw new JsonException($"Unsupported AMQP header value for {pair.Key}.")
        });
        var commandId = Encoding.UTF8.GetString(headers["sf-ex-command-id"]);
        if (!await approvals.RegisterAsync(request.Id, request.TotalQuantity, commandId, replyTo,
                command.Headers.CorrelationId, JsonSerializer.Serialize(headers), cancellationToken))
            throw new JsonException("A duplicate approval command conflicts with its stored request.");

        return null;
    }

    private sealed record ApprovalRequest(Guid Id, int TotalQuantity);
}
