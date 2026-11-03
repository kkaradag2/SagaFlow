using SagaFlow.Payment.Domain;
using SagaFlow.Payment.Repositories;

namespace SagaFlow.Payment.Services;

public sealed class PaymentService(IPaymentRepository repository) : IPaymentService
{
    public ReturnRefund RefundReturnPayment(string returnId, string orderId, decimal amount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(returnId);
        Validate(orderId, amount);
        return repository.UpsertReturnRefund(returnId, current => current switch
        {
            null => new ReturnRefund(returnId, orderId, amount),
            { OrderId: var existingOrder, Amount: var existingAmount }
                when existingOrder != orderId || existingAmount != amount =>
                    throw new InvalidOperationException("A different refund already exists for this return."),
            _ => current
        });
    }
    public PaymentAuthorization AuthorizePayment(string orderId, decimal amount)
    {
        Validate(orderId, amount);
        return repository.Upsert(orderId, current => current switch
        {
            null => new(orderId, amount, PaymentStatus.Authorized),
            { Amount: var existing } when existing != amount => throw new InvalidOperationException("An authorization with a different amount already exists."),
            { Status: PaymentStatus.Refunded } => throw new InvalidOperationException("A refunded authorization cannot be authorized again."),
            _ => current
        });
    }

    public PaymentAuthorization RefundPayment(string orderId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);
        return repository.Upsert(orderId, current => current switch
        {
            null => throw new InvalidOperationException("Payment authorization was not found."),
            { Status: PaymentStatus.Refunded } => current,
            _ => current with { Status = PaymentStatus.Refunded }
        });
    }

    private static void Validate(string orderId, decimal amount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be positive.");
    }
}
