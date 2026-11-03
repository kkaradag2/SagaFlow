using SagaFlow.Inventory.Domain;

namespace SagaFlow.Inventory.Repositories;

public interface IInventoryRepository
{
    InventoryReservation? Find(string orderId);
    InventoryReservation Upsert(string orderId, Func<InventoryReservation?, InventoryReservation> update);
    ReturnRestock? FindReturnRestock(string returnId);
    ReturnRestock UpsertReturnRestock(string returnId, Func<ReturnRestock?, ReturnRestock> update);
}
