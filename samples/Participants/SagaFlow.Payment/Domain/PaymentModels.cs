namespace SagaFlow.Payment.Domain;

public enum PaymentStatus { Authorized, Refunded }
public sealed record PaymentAuthorization(string OrderId, decimal Amount, PaymentStatus Status);
