using System.Collections.Concurrent;
using SagaFlow.Notification.Domain;

namespace SagaFlow.Notification.Repositories;

public sealed class InMemoryNotificationRepository : INotificationRepository
{
    private readonly ConcurrentDictionary<string, OrderNotification> _notifications = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ManagerNotification> _managerNotifications = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ReturnNotification> _returnNotifications = new(StringComparer.Ordinal);
    public ReturnNotification? FindReturn(string returnId) => _returnNotifications.GetValueOrDefault(returnId);
    public ReturnNotification AddOrGet(ReturnNotification notification) =>
        _returnNotifications.AddOrUpdate(notification.ReturnId, notification, (_, current) =>
            current == notification ? current : throw new InvalidOperationException("A different notification already exists for this return."));
    public ManagerNotification? FindManager(string orderId) => _managerNotifications.GetValueOrDefault(orderId);
    public ManagerNotification AddOrGet(ManagerNotification notification) =>
        _managerNotifications.AddOrUpdate(notification.OrderId, notification, (_, current) =>
            current == notification ? current : throw new InvalidOperationException("A different manager notification already exists for this order."));
    public OrderNotification? Find(string orderId) => _notifications.GetValueOrDefault(orderId);
    public OrderNotification AddOrGet(OrderNotification notification) =>
        _notifications.AddOrUpdate(notification.OrderId, notification, (_, current) =>
            current == notification ? current : throw new InvalidOperationException("A different notification already exists for this order."));
}
