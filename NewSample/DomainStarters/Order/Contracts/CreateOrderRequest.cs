using System.ComponentModel.DataAnnotations;

namespace Sample.DomainStarters.Order.Contracts;

public sealed record CreateOrderRequest(
    [Required, StringLength(100)] string CustomerId,
    [Required, MinLength(1), MaxLength(100)] IReadOnlyList<CreateOrderItemRequest> Items)
    : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Items is not null && Items.Any(item => item is null))
        {
            yield return new ValidationResult("Order items cannot be null.", [nameof(Items)]);
        }
    }
}

public sealed record CreateOrderItemRequest(
    [Required, StringLength(100)] string ProductId,
    [Range(1, 10000)] int Quantity,
    [Range(typeof(decimal), "0.01", "999999.99", ParseLimitsInInvariantCulture = true)]
    decimal UnitPrice) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (UnitPrice != decimal.Round(UnitPrice, 2))
        {
            yield return new ValidationResult("Unit price must have at most two decimal places.", [nameof(UnitPrice)]);
        }
    }
}
