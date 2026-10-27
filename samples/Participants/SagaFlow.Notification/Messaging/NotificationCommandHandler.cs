using SagaFlow.Core.Messages;
using SagaFlow.Notification.Services;
using SagaFlow.Participants.Messaging;

namespace SagaFlow.Notification.Messaging;

public sealed class NotificationCommandHandler(INotificationService notifications) : IParticipantCommandHandler
{
    public IReadOnlyCollection<string> CommandNames { get; } = ["SendOrderNotification"];

    public object Handle(SagaCommand command)
    {
        if (command.Name != "SendOrderNotification")
            throw new InvalidOperationException($"Unsupported notification command '{command.Name}'.");

        return notifications.SendOrderNotification(
            command.Payload.GetProperty("id").GetGuid().ToString("N"),
            command.Payload.GetProperty("customerId").GetString()!);
    }
}
