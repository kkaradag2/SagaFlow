using System.Collections.Concurrent;
using SagaFlow.Inventory.Domain;

namespace SagaFlow.Inventory.Repositories;

public sealed class InMemoryInventoryRepository : IInventoryRepository
{
    private readonly ConcurrentDictionary<string, InventoryReservation> _reservations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ReturnRestock> _restocks = new(StringComparer.Ordinal);
    public InventoryReservation? Find(string orderId) => _reservations.GetValueOrDefault(orderId);
    public InventoryReservation Upsert(string orderId, Func<InventoryReservation?, InventoryReservation> update) =>
        _reservations.AddOrUpdate(orderId, _ => update(null), (_, current) => update(current));
    public ReturnRestock? FindReturnRestock(string returnId) => _restocks.GetValueOrDefault(returnId);
    public ReturnRestock UpsertReturnRestock(string returnId, Func<ReturnRestock?, ReturnRestock> update) =>
        _restocks.AddOrUpdate(returnId, _ => update(null), (_, current) => update(current));
}
