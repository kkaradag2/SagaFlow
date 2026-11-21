using System.Text.Json;
using SagaFlow.Participants.Messaging;

namespace Sample.Participants.Messaging;

internal static class ParticipantOutcomeFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static ParticipantOutcome Create(ParticipantCommand command, Guid orderId, string? error)
        => Create(command, new { orderId, error }, error is null);

    public static ParticipantOutcome Create(ParticipantCommand command, Guid returnId, Guid orderId, string? error)
        => Create(command, new { returnId, orderId, error }, error is null);

    private static ParticipantOutcome Create<T>(ParticipantCommand command, T result, bool success) => new()
    {
        Name = command.Name + (success ? "Completed" : "Failed"),
        IsSuccess = success,
        Headers = command.Headers,
        Result = JsonSerializer.SerializeToElement(result, JsonOptions)
    };
}
