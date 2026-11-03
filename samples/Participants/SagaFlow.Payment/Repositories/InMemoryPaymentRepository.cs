using System.Collections.Concurrent;
using SagaFlow.Payment.Domain;

namespace SagaFlow.Payment.Repositories;

public sealed class InMemoryPaymentRepository : IPaymentRepository
{
    private readonly ConcurrentDictionary<string, PaymentAuthorization> _authorizations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ReturnRefund> _returnRefunds = new(StringComparer.Ordinal);
    public PaymentAuthorization? Find(string orderId) => _authorizations.GetValueOrDefault(orderId);
    public PaymentAuthorization Upsert(string orderId, Func<PaymentAuthorization?, PaymentAuthorization> update) =>
        _authorizations.AddOrUpdate(orderId, _ => update(null), (_, current) => update(current));
    public ReturnRefund? FindReturnRefund(string returnId) => _returnRefunds.GetValueOrDefault(returnId);
    public ReturnRefund UpsertReturnRefund(string returnId, Func<ReturnRefund?, ReturnRefund> update) =>
        _returnRefunds.AddOrUpdate(returnId, _ => update(null), (_, current) => update(current));
}
