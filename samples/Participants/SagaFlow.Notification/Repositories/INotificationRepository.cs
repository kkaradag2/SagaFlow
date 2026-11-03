using SagaFlow.Notification.Domain;

namespace SagaFlow.Notification.Repositories;

public interface INotificationRepository
{
    OrderNotification? Find(string orderId);
    OrderNotification AddOrGet(OrderNotification notification);
    ManagerNotification? FindManager(string orderId);
    ManagerNotification AddOrGet(ManagerNotification notification);
    ReturnNotification? FindReturn(string returnId);
    ReturnNotification AddOrGet(ReturnNotification notification);
}
