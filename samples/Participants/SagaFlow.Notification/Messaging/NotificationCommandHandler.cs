using SagaFlow.Participants.Messaging;
using SagaFlow.Notification.Services;

namespace SagaFlow.Notification.Messaging;

public sealed class NotificationCommandHandler(INotificationService notifications) : IParticipantCommandHandler
{
    public IReadOnlyCollection<string> CommandNames { get; } = ["SendOrderNotification", "NotifyManager", "NotifyReturnAccepted", "NotifyReturnRejected"];

    public ParticipantCommandResult Handle(ParticipantCommand command, string? replyTo = null)
    {
        if (command.Name is "NotifyReturnAccepted" or "NotifyReturnRejected")
        {
            if (command.Headers.IsCompensation) throw new InvalidOperationException("Return notification is a forward operation.");
            if (SampleFailureInjection.IsRequested(command, "notification", command.Name))
                return ParticipantCommandResult.Failure("notification");
            var request = command.Bind<NotifyReturnRequest>();
            return ParticipantCommandResult.Success(notifications.NotifyReturn(
                request.ReturnId.ToString("N"), request.OrderId.ToString("N"), request.CustomerId,
                command.Name == "NotifyReturnAccepted"));
        }
        if (command.Name == "NotifyManager")
        {
            var request = command.Bind<NotifyManagerRequest>();
            return ParticipantCommandResult.Success(notifications.NotifyManager(request.Id.ToString("N"), request.TotalQuantity));
        }

        if (command.Name != "SendOrderNotification")
            throw new InvalidOperationException($"Unsupported notification command '{command.Name}'.");

        if (SampleFailureInjection.IsRequested(command, "notification", "SendOrderNotification"))
            return ParticipantCommandResult.Failure("notification");

        var order = command.Bind<SendOrderNotificationRequest>();
        return ParticipantCommandResult.Success(notifications.SendOrderNotification(order.Id.ToString("N"), order.CustomerId));
    }
}
