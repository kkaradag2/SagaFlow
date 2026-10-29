using SagaFlow.Order.Domain;
using DomainOrder = SagaFlow.Order.Domain.Order;

namespace SagaFlow.Order.Repositories;

public interface IOrderRepository
{
    ValueTask AddAsync(DomainOrder order, CancellationToken cancellationToken = default);
    ValueTask<DomainOrder?> FindAsync(Guid id, CancellationToken cancellationToken = default);
    ValueTask CancelAsync(Guid id, CancellationToken cancellationToken = default);
}
