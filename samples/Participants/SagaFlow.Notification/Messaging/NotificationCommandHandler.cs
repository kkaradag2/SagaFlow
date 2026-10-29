using SagaFlow.Core.Messages;
using SagaFlow.Notification.Services;
using SagaFlow.Participants.Messaging;

namespace SagaFlow.Notification.Messaging;

public sealed class NotificationCommandHandler(INotificationService notifications) : IParticipantCommandHandler
{
    public IReadOnlyCollection<string> CommandNames { get; } = ["SendOrderNotification", "NotifyManager"];

    public ParticipantCommandResult Handle(SagaCommand command, string? replyTo = null)
    {
        if (command.Name == "NotifyManager")
            return ParticipantCommandResult.Success(notifications.NotifyManager(
                command.Payload.GetProperty("id").GetGuid().ToString("N"),
                command.Payload.GetProperty("totalQuantity").GetInt64()));

        if (command.Name != "SendOrderNotification")
            throw new InvalidOperationException($"Unsupported notification command '{command.Name}'.");

        if (SampleFailureInjection.IsRequested(command, "notification", "SendOrderNotification"))
            return ParticipantCommandResult.Failure("notification");

        return ParticipantCommandResult.Success(notifications.SendOrderNotification(
            command.Payload.GetProperty("id").GetGuid().ToString("N"),
            command.Payload.GetProperty("customerId").GetString()!));
    }
}
