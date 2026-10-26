namespace SagaFlow.Order.Domain;

public sealed record CreateOrderItem(string ProductId, int Quantity, decimal UnitPrice);

public sealed record CreateOrderRequest(string CustomerId, IReadOnlyList<CreateOrderItem> Items);

public sealed record OrderItem(string ProductId, int Quantity, decimal UnitPrice)
{
    public decimal LineTotal => Quantity * UnitPrice;
}

public sealed record Order(Guid Id, string CustomerId, IReadOnlyList<OrderItem> Items, decimal Total, DateTimeOffset CreatedAt);

public sealed record CreateOrderResult(Guid OrderId, Guid SagaInstanceId);
