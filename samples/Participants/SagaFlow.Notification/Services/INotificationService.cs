using SagaFlow.Notification.Domain;

namespace SagaFlow.Notification.Services;

public interface INotificationService
{
    OrderNotification SendOrderNotification(string orderId, string recipient);
    ManagerNotification NotifyManager(string orderId, long totalQuantity);
}
