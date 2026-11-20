using Sample.DomainStarters.Order.Contracts;
using Sample.DomainStarters.Order.Data;
using Sample.DomainStarters.Order.Domain;

namespace Sample.DomainStarters.Order.Services;

internal sealed class OrderService(PostgreSqlOrderRepository repository, ILogger<OrderService> logger) : IOrderService
{
    public async Task<OrderDetails> CreateAsync(CreateOrderRequest request, CancellationToken cancellationToken)
    {
        var items = request.Items
            .Select(item => new OrderItem(item.ProductId, item.Quantity, item.UnitPrice))
            .ToArray();
        var order = new OrderDetails(
            Guid.NewGuid(),
            request.CustomerId,
            items,
            items.Sum(item => item.Quantity * item.UnitPrice),
            OrderStatus.Created,
            DateTimeOffset.UtcNow);

        await repository.AddAsync(order, cancellationToken);
        logger.LogInformation("Order {OrderId} created with {ItemCount} items", order.Id, items.Length);
        return order;
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
