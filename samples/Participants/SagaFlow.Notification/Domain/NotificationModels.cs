namespace SagaFlow.Notification.Domain;

public sealed record OrderNotification(string OrderId, string Recipient, string Message);

public sealed record ManagerNotification(string OrderId, long TotalQuantity, string Message);
public sealed record ReturnNotification(string ReturnId, string OrderId, string Recipient, string Decision, string Message);
