using Sample.DomainStarters.Order.Contracts;
using Sample.DomainStarters.Order.Domain;

namespace Sample.DomainStarters.Order.Services;

public interface IOrderService
{
    Task<OrderDetails> CreateAsync(CreateOrderRequest request, CancellationToken cancellationToken);

    Task<bool> CancelAsync(Guid orderId, CancellationToken cancellationToken);
}
