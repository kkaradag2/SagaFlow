using System.Text;
using System.Text.Json;
using Npgsql;
using SagaFlow.Participants.Messaging;
using Sample.Participants.Warehouse.Data;
using Sample.Participants.Messaging;

namespace Sample.Participants.Warehouse.Messaging;

internal sealed class InspectionCommandHandler(InspectionRepository inspections) : IParticipantCommandHandler
{
    public async Task<ParticipantOutcome?> HandleAsync(
        ParticipantCommand command, string replyTo, CancellationToken cancellationToken)
    {
        if (command.Name != "InspectReturn" || command.Headers.IsCompensation)
            throw new JsonException("Only forward InspectReturn commands are supported.");

        var request = command.Bind<InspectionRequest>();
        if (request.ReturnId == Guid.Empty || request.OrderId == Guid.Empty)
            throw new JsonException("Warehouse inspection requires a return ID and an order ID.");

        if (string.Equals(request.FailStep?.Trim(), "warehouse", StringComparison.OrdinalIgnoreCase))
        {
            return new ParticipantOutcome
            {
                Name = "InspectReturnFailed",
                IsSuccess = false,
                Headers = command.Headers,
                Result = JsonSerializer.SerializeToElement(new { error = "Simulated warehouse failure." })
            };
        }

        // JSON stores byte arrays as base64, preserving the original execution snapshot.
        var headers = command.Headers.Headers.ToDictionary(pair => pair.Key, pair => pair.Value switch
        {
            byte[] bytes => bytes,
            string value => Encoding.UTF8.GetBytes(value),
            _ => throw new JsonException($"Unsupported AMQP header value for {pair.Key}.")
        });
        var commandId = Encoding.UTF8.GetString(headers["sf-ex-command-id"]);
        try
        {
            if (!await inspections.RegisterAsync(request.ReturnId, request.OrderId, commandId, replyTo,
                    command.Headers.CorrelationId, JsonSerializer.Serialize(headers), cancellationToken))
                throw new JsonException("A duplicate inspection command conflicts with its stored request.");
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation &&
                                              error.ConstraintName == "requests_command_id_key")
        {
            throw new JsonException("The inspection command ID already belongs to another return.", error);
        }

        return null;
    }

    private sealed record InspectionRequest(Guid ReturnId, Guid OrderId, string? FailStep = null);
}
