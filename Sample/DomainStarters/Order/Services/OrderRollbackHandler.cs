using System.Text.Json;
using SagaFlow.Core.Abstractions;

namespace Sample.DomainStarters.Order.Services;

internal sealed class OrderRollbackHandler<TPayload>(IOrderService orders) : SagaRollbackHandler<TPayload>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public override async Task RollbackAsync(TPayload payload, CancellationToken cancellationToken)
    {
        var context = JsonSerializer.SerializeToElement(payload, JsonOptions);
        if (context.ValueKind != JsonValueKind.Object ||
            !context.TryGetProperty("id", out var id) ||
            id.ValueKind != JsonValueKind.String ||
            !id.TryGetGuid(out var orderId) || orderId == Guid.Empty)
            throw new InvalidOperationException("Order rollback payload must contain a valid order ID.");

        if (!await orders.CancelAsync(orderId, cancellationToken))
            throw new InvalidOperationException($"Order '{orderId}' could not be found for rollback.");
    }
}
