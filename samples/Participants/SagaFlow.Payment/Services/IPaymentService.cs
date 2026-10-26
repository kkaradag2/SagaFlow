using SagaFlow.Payment.Domain;

namespace SagaFlow.Payment.Services;

public interface IPaymentService
{
    PaymentAuthorization AuthorizePayment(string orderId, decimal amount);
    PaymentAuthorization RefundPayment(string orderId);
}
