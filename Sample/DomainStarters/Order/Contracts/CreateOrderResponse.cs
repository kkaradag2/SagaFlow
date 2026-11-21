using Sample.DomainStarters.Order.Domain;

namespace Sample.DomainStarters.Order.Contracts;

public sealed record CreateOrderResponse(
    Guid OrderId,
    Guid SagaInstanceId,
    string CustomerId,
    IReadOnlyList<OrderItem> Items,
    decimal Total,
    OrderStatus Status,
    DateTimeOffset CreatedAt)
{
    public Guid Id => OrderId;
}
