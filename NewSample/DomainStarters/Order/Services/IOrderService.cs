using Sample.DomainStarters.Order.Contracts;

namespace Sample.DomainStarters.Order.Services;

public interface IOrderService
{
    Task<CreateOrderResponse> CreateAsync(CreateOrderRequest request, CancellationToken cancellationToken);

    Task<bool> CancelAsync(Guid orderId, CancellationToken cancellationToken);
}
