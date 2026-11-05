using FluentValidation;
using SagaFlow.Order.Domain;
using SagaFlow.Order.Validators;
using DomainOrder = SagaFlow.Order.Domain.Order;

namespace SagaFlow.Order.Services;

/// <summary>One domain creation path for direct API requests and admitted incoming invocations.</summary>
public static class OrderCreation
{
    public static DomainOrder Create(CreateOrderRequest request, DateTimeOffset now)
    {
        new CreateOrderRequestValidator().ValidateAndThrow(request);
        var items = request.Items.Select(item => new OrderItem(item.ProductId.Trim(), item.Quantity, item.UnitPrice)).ToArray();
        decimal total;
        try { total = items.Sum(item => item.LineTotal); }
        catch (OverflowException) { throw new ValidationException("Order total exceeds the supported amount range."); }
        return new(Guid.NewGuid(), request.CustomerId.Trim(), Array.AsReadOnly(items), total, now, request.FailureAt);
    }
}
