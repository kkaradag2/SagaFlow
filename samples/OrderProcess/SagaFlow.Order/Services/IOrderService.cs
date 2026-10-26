using SagaFlow.Order.Domain;

namespace SagaFlow.Order.Services;

public interface IOrderService
{
    Task<CreateOrderResult> CreateAsync(CreateOrderRequest request, CancellationToken cancellationToken = default);
}
