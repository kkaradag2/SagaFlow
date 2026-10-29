namespace SagaFlow.Notification.Domain;

public sealed record OrderNotification(string OrderId, string Recipient, string Message);

public sealed record ManagerNotification(string OrderId, long TotalQuantity, string Message);
