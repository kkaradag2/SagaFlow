namespace SagaFlow.Notification.Domain;

public sealed record OrderNotification(string OrderId, string Recipient, string Message);
