using SagaFlow.Inventory.Domain;
using SagaFlow.Inventory.Repositories;

namespace SagaFlow.Inventory.Services;

public sealed class InventoryService(IInventoryRepository repository) : IInventoryService
{
    public InventoryReservation ReserveInventory(string orderId, IReadOnlyList<InventoryLine> lines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);
        if (lines is not { Count: > 0 } || lines.Any(line => line is null || string.IsNullOrWhiteSpace(line.ProductId) || line.Quantity <= 0))
            throw new ArgumentException("At least one valid inventory line is required.", nameof(lines));
        var snapshot = Array.AsReadOnly(lines.Select(line => new InventoryLine(line.ProductId.Trim(), line.Quantity)).ToArray());
        return repository.Upsert(orderId, current => current switch
        {
            null => new(orderId, snapshot, ReservationStatus.Reserved),
            { Status: ReservationStatus.Released } => throw new InvalidOperationException("A released reservation cannot be reserved again."),
            _ when !current.Lines.SequenceEqual(snapshot) => throw new InvalidOperationException("A different reservation already exists for this order."),
            _ => current
        });
    }

    public InventoryReservation ReleaseInventory(string orderId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);
        return repository.Upsert(orderId, current => current switch
        {
            null => throw new InvalidOperationException("Inventory reservation was not found."),
            { Status: ReservationStatus.Released } => current,
            _ => current with { Status = ReservationStatus.Released }
        });
    }
}
