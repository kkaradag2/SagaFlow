using System.Collections.Concurrent;
using SagaFlow.Order.Domain;
using DomainOrder = SagaFlow.Order.Domain.Order;

namespace SagaFlow.Order.Repositories;

public sealed class InMemoryOrderRepository : IOrderRepository
{
    private readonly ConcurrentDictionary<Guid, DomainOrder> _orders = new();

    public ValueTask AddAsync(DomainOrder order, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_orders.TryAdd(order.Id, order)) throw new InvalidOperationException($"Order '{order.Id}' already exists.");
        return ValueTask.CompletedTask;
    }

    public ValueTask<DomainOrder?> FindAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_orders.GetValueOrDefault(id));
    }
}
