using Microsoft.Extensions.Logging;
using Sample.Participants.Inventory.Data;
using Sample.Participants.Inventory.Domain;

namespace Sample.Participants.Inventory.Services;

internal sealed class InventoryService(InventoryRepository repository, ILogger<InventoryService> logger)
{
    public async Task<string?> ReserveAsync(
        Guid orderId, IReadOnlyList<InventoryLine> items, string? failStep, CancellationToken cancellationToken)
    {
        if (orderId == Guid.Empty || items is not { Count: >= 1 and <= 100 } ||
            items.Any(item => item is null || string.IsNullOrWhiteSpace(item.ProductId) ||
                item.ProductId.Length > 100 || item.Quantity is < 1 or > 10000))
            return "invalid_inventory_request";

        if (string.Equals(failStep?.Trim(), "inventory", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Inventory failure requested for order {OrderId}", orderId);
            return "inventory_reservation_failed";
        }

        // Aggregate repeated product IDs and lock stock rows in the same order for reserve and release.
        var lines = items.GroupBy(item => item.ProductId, StringComparer.Ordinal)
            .Select(group => new InventoryLine(group.Key, group.Sum(item => item.Quantity)))
            .OrderBy(line => line.ProductId, StringComparer.Ordinal)
            .ToArray();

        var result = await repository.ReserveAsync(orderId, lines, cancellationToken);
        if (result == ReservationResult.Reserved)
            logger.LogInformation("Inventory reservation confirmed for order {OrderId}", orderId);

        return result switch
        {
            ReservationResult.Reserved => null,
            ReservationResult.InsufficientStock => "insufficient_stock",
            ReservationResult.Conflict => "inventory_reservation_conflict",
            _ => throw new InvalidOperationException($"Unexpected reservation result: {result}.")
        };
    }

    public async Task<string?> ReleaseAsync(Guid orderId, CancellationToken cancellationToken)
    {
        if (orderId == Guid.Empty)
            return "invalid_inventory_request";

        if (!await repository.ReleaseAsync(orderId, cancellationToken))
            return "inventory_reservation_not_found";

        logger.LogInformation("Inventory release confirmed for order {OrderId}", orderId);
        return null;
    }
}
