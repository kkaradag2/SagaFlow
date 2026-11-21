using System.ComponentModel.DataAnnotations;
using Sample.DomainStarters.Order.Contracts;

namespace Sample.DomainStarters.Procurement.Contracts;

public sealed record CreatePurchaseRequest(
    [Required, StringLength(100)] string EmployeeId,
    [Required, StringLength(100)] string CustomerId,
    [Required, MinLength(1), MaxLength(100)] IReadOnlyList<CreateOrderItemRequest> Items,
    [StringLength(100)] string? FailStep = null) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Items?.Any(item => item is null) == true)
            yield return new ValidationResult("Purchase items cannot be null.", [nameof(Items)]);
    }
}

internal sealed record PurchasePayload(
    Guid Id, string EmployeeId, string CustomerId, IReadOnlyList<CreateOrderItemRequest> Items, string? FailStep)
{
    public int TotalQuantity => Items.Sum(item => item.Quantity);
}

public sealed record PurchaseCreated(Guid PurchaseRequestId, Guid SagaInstanceId);

public sealed record PurchaseStatusResponse(
    Guid PurchaseRequestId, Guid? SagaInstanceId, string EmployeeId, string Status, string? SagaStatus,
    string? SourceCorrelationId, string? InvocationId, string? OrderSagaInstanceId, string? LastKnownOrderStatus);
