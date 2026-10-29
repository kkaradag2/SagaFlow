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

    public ValueTask CancelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (true)
        {
            if (!_orders.TryGetValue(id, out var order)) throw new KeyNotFoundException($"Order '{id}' was not found.");
            if (order.Status == OrderStatus.Cancelled) return ValueTask.CompletedTask;
            if (_orders.TryUpdate(id, order with { Status = OrderStatus.Cancelled }, order)) return ValueTask.CompletedTask;
        }
    }
}
