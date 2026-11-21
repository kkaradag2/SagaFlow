using System.ComponentModel.DataAnnotations;

namespace Sample.DomainStarters.Return.Contracts;

public sealed record CreateReturnRequest(
    Guid OrderId,
    [Required, StringLength(100)] string CustomerId,
    [Range(typeof(decimal), "0.01", "9999999999.99")] decimal Amount,
    [Required, MinLength(1), MaxLength(100)] IReadOnlyList<ReturnItemRequest> Items,
    [StringLength(100)] string? FailStep = null) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (OrderId == Guid.Empty)
            yield return new ValidationResult("OrderId must be a non-empty UUID.", [nameof(OrderId)]);

        if (decimal.Round(Amount, 2) != Amount)
            yield return new ValidationResult("Amount must have at most two decimal places.", [nameof(Amount)]);

        if (Items?.Any(item => item is null) == true)
            yield return new ValidationResult("Items must not contain null entries.", [nameof(Items)]);
    }
}

public sealed record ReturnItemRequest(
    [Required, StringLength(100)] string ProductId,
    [Range(1, 10000)] int Quantity);
