using SagaFlow.Core.Abstractions;
using SagaFlow.Order.Domain;
using SagaFlow.Order.Repositories;

namespace SagaFlow.Order.Services;

public sealed class OrderService(
    IOrderRepository repository,
    ISagaFlow sagaFlow,
    TimeProvider timeProvider) : IOrderService
{
    public OrderService(IOrderRepository repository, ISagaFlow sagaFlow)
        : this(repository, sagaFlow, TimeProvider.System)
    {
    }

    public async Task<CreateOrderResult> CreateAsync(CreateOrderRequest request, CancellationToken cancellationToken = default)
    {
        var order = OrderCreation.Create(request, timeProvider.GetUtcNow());

        await repository.AddAsync(order, cancellationToken);

        var sagaInstanceId = await sagaFlow.StartAsync(order, cancellationToken);

        return new(order.Id, sagaInstanceId);
    }

    public async Task CancelAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        await repository.CancelAsync(orderId, cancellationToken);
}
