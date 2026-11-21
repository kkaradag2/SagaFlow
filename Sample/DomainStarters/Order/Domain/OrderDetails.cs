using System.Text.Json.Serialization;

namespace Sample.DomainStarters.Order.Domain;

public sealed record OrderDetails(
    Guid Id,
    string CustomerId,
    IReadOnlyList<OrderItem> Items,
    decimal Total,
    OrderStatus Status,
    DateTimeOffset CreatedAt);

public sealed record OrderItem(string ProductId, int Quantity, decimal UnitPrice);

[JsonConverter(typeof(JsonStringEnumConverter<OrderStatus>))]
public enum OrderStatus
{
    Created,
    Cancelled
}
