using SagaFlow.Payment.Domain;

namespace SagaFlow.Payment.Repositories;

public interface IPaymentRepository
{
    PaymentAuthorization? Find(string orderId);
    PaymentAuthorization Upsert(string orderId, Func<PaymentAuthorization?, PaymentAuthorization> update);
    ReturnRefund UpsertReturnRefund(string returnId, Func<ReturnRefund?, ReturnRefund> update);
    ReturnRefund? FindReturnRefund(string returnId);
}
