using System.Collections.Concurrent;
using SagaFlow.Notification.Domain;

namespace SagaFlow.Notification.Repositories;

public sealed class InMemoryNotificationRepository : INotificationRepository
{
    private readonly ConcurrentDictionary<string, OrderNotification> _notifications = new(StringComparer.Ordinal);
    public OrderNotification? Find(string orderId) => _notifications.GetValueOrDefault(orderId);
    public OrderNotification AddOrGet(OrderNotification notification) =>
        _notifications.AddOrUpdate(notification.OrderId, notification, (_, current) =>
            current == notification ? current : throw new InvalidOperationException("A different notification already exists for this order."));
}
