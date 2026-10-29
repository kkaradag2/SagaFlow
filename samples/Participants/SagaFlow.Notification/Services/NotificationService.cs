using SagaFlow.Notification.Domain;
using SagaFlow.Notification.Repositories;

namespace SagaFlow.Notification.Services;

public sealed class NotificationService(INotificationRepository repository) : INotificationService
{
    public ManagerNotification NotifyManager(string orderId, long totalQuantity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);
        if (totalQuantity <= 0) throw new ArgumentOutOfRangeException(nameof(totalQuantity));
        var notification = new ManagerNotification(orderId, totalQuantity,
            $"Manager approval requested for order {orderId}: total quantity {totalQuantity}.");
        var stored = repository.AddOrGet(notification);
        if (ReferenceEquals(stored, notification)) Console.WriteLine(notification.Message);
        return stored;
    }

    public OrderNotification SendOrderNotification(string orderId, string recipient)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipient);
        var normalizedRecipient = recipient.Trim();
        return repository.AddOrGet(new OrderNotification(orderId, normalizedRecipient, $"Order {orderId} has been completed."));
    }
}
