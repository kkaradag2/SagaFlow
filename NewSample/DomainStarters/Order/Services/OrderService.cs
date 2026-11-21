using SagaFlow.Core.Abstractions;
using SagaFlow.Engine.Coordination;
using Sample.DomainStarters.Order.Contracts;
using Sample.DomainStarters.Order.Data;

namespace Sample.DomainStarters.Order.Services;

internal sealed class OrderService(
    PostgreSqlOrderRepository repository, ISagaFlow sagaFlow, ILogger<OrderService> logger) : IOrderService
{
    public async Task<CreateOrderResponse> CreateAsync(CreateOrderRequest request, CancellationToken cancellationToken)
    {
        var order = OrderCreation.Create(request);

        await repository.AddAsync(order, cancellationToken);
        logger.LogInformation("Order {OrderId} created with {ItemCount} items", order.Id, order.Items.Count);

        Guid sagaInstanceId;
        try
        {
            // A client disconnect must not cancel Saga startup after the order commit.
            // Retain the persisted payload type used by existing Order Sagas for rollback.
            sagaInstanceId = await sagaFlow.StartAsync(new
            {
                order.Id,
                order.CustomerId,
                order.Items,
                order.Total,
                TotalQuantity = order.Items.Sum(item => item.Quantity),
                FailStep = request.FailStep?.Trim()
            }, CancellationToken.None);
        }
        catch (Exception error)
        {
            Guid? knownSagaId = error is SagaDispatchException dispatch &&
                Guid.TryParse(dispatch.InstanceId, out var instanceId) ? instanceId : null;
            logger.LogError("Saga startup was not confirmed for order {OrderId}, saga {SagaInstanceId}: {ErrorType}",
                order.Id, knownSagaId, error.GetType().Name);
            throw new OrderSagaStartException(order.Id, knownSagaId, error);
        }

        logger.LogInformation("Order {OrderId} started Saga {SagaInstanceId}", order.Id, sagaInstanceId);
        return new(order.Id, sagaInstanceId, order.CustomerId, order.Items, order.Total, order.Status, order.CreatedAt);
    }

    public async Task<bool> CancelAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var cancelled = await repository.CancelAsync(orderId, cancellationToken);
        if (cancelled)
        {
            logger.LogInformation("Order {OrderId} is cancelled", orderId);
        }

        return cancelled;
    }
}
