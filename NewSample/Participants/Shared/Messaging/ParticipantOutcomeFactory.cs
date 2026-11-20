using System.Text.Json;
using SagaFlow.Participants.Messaging;

namespace Sample.Participants.Messaging;

internal static class ParticipantOutcomeFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static ParticipantOutcome Create(ParticipantCommand command, Guid orderId, string? error) => new()
    {
        Name = command.Name + (error is null ? "Completed" : "Failed"),
        IsSuccess = error is null,
        Headers = command.Headers,
        Result = JsonSerializer.SerializeToElement(new { orderId, error }, JsonOptions)
    };
}
