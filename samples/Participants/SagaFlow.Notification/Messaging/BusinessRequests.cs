namespace SagaFlow.Notification.Messaging;

public sealed record SendOrderNotificationRequest(Guid Id, string CustomerId);
public sealed record NotifyManagerRequest(Guid Id, long TotalQuantity);
// Both return notification commands share the same business schema.
public sealed record NotifyReturnRequest(Guid ReturnId, Guid OrderId, string CustomerId);
