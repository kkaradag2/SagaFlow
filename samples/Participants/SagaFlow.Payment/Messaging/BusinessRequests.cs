namespace SagaFlow.Payment.Messaging;

public sealed record AuthorizePaymentRequest(Guid Id, decimal Total);
public sealed record RefundPaymentRequest(Guid Id);
public sealed record RefundReturnPaymentRequest(Guid ReturnId, Guid OrderId, decimal Amount);
