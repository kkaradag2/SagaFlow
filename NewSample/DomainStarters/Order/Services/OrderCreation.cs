using Sample.DomainStarters.Order.Contracts;
using Sample.DomainStarters.Order.Domain;

namespace Sample.DomainStarters.Order.Services;

internal static class OrderCreation
{
    public static OrderDetails Create(CreateOrderRequest request)
    {
        var items = request.Items.Select(item => new OrderItem(item.ProductId, item.Quantity, item.UnitPrice)).ToArray();
        return new(Guid.NewGuid(), request.CustomerId, items, items.Sum(item => item.Quantity * item.UnitPrice),
            OrderStatus.Created, DateTimeOffset.UtcNow);
    }

    public static OrderSagaPayload CreatePayload(OrderDetails order, string? failStep)
        => new(order.Id, order.CustomerId, order.Items, order.Total, order.Items.Sum(item => item.Quantity), failStep?.Trim());
}

internal sealed record OrderSagaPayload(
    Guid Id, string CustomerId, IReadOnlyList<OrderItem> Items, decimal Total, int TotalQuantity, string? FailStep);
