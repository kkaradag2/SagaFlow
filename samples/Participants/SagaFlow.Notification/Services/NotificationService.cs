using SagaFlow.Notification.Domain;
using SagaFlow.Notification.Repositories;

namespace SagaFlow.Notification.Services;

public sealed class NotificationService(INotificationRepository repository) : INotificationService
{
    public OrderNotification SendOrderNotification(string orderId, string recipient)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipient);
        var normalizedRecipient = recipient.Trim();
        return repository.AddOrGet(new(orderId, normalizedRecipient, $"Order {orderId} has been completed."));
    }
}
