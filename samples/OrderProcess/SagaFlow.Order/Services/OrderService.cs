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
        var items = request.Items
            .Select(item => new OrderItem(item.ProductId.Trim(), item.Quantity, item.UnitPrice))
            .ToArray();

        var order = new Domain.Order(
            Guid.NewGuid(),
            request.CustomerId.Trim(),
            Array.AsReadOnly(items),
            items.Sum(item => item.LineTotal),
            timeProvider.GetUtcNow(),
            request.FailureAt);

        await repository.AddAsync(order, cancellationToken);

        var sagaInstanceId = await sagaFlow.StartAsync(order, cancellationToken);

        return new(order.Id, sagaInstanceId);
    }
}
