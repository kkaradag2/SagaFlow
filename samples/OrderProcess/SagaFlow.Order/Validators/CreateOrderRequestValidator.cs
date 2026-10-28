using FluentValidation;
using SagaFlow.Order.Domain;

namespace SagaFlow.Order.Validators;

public sealed class CreateOrderRequestValidator : AbstractValidator<CreateOrderRequest>
{
    private static readonly HashSet<string> ValidFailureTargets =
        ["payment", "inventory", "shipment", "notification"];

    public CreateOrderRequestValidator()
    {
        RuleFor(request => request.CustomerId)
            .NotEmpty()
            .WithMessage("CustomerId is required.");

        RuleFor(request => request.Items)
            .NotEmpty()
            .WithMessage("At least one order item is required.");

        RuleFor(request => request.FailureAt)
            .Must(value => value is null || ValidFailureTargets.Contains(value))
            .WithMessage("FailureAt must be one of: payment, inventory, shipment, notification.");

        RuleForEach(request => request.Items)
            .NotNull()
            .WithMessage("Every item requires a product id, positive quantity, and positive unit price.");

        RuleForEach(request => request.Items)
            .Where(item => item is not null)
            .ChildRules(item =>
            {
                item.RuleFor(value => value!.ProductId)
                    .NotEmpty()
                    .WithMessage("Every item requires a product id, positive quantity, and positive unit price.");
                item.RuleFor(value => value!.Quantity)
                    .GreaterThan(0)
                    .WithMessage("Every item requires a product id, positive quantity, and positive unit price.");
                item.RuleFor(value => value!.UnitPrice)
                    .GreaterThan(0)
                    .WithMessage("Every item requires a product id, positive quantity, and positive unit price.");
            });
    }
}
