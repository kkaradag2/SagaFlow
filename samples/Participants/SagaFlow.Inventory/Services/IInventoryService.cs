using SagaFlow.Inventory.Domain;

namespace SagaFlow.Inventory.Services;

public interface IInventoryService
{
    InventoryReservation ReserveInventory(string orderId, IReadOnlyList<InventoryLine> lines);
    InventoryReservation ReleaseInventory(string orderId);
    ReturnRestock RestockReturn(string returnId, string orderId, IReadOnlyList<InventoryLine> lines);
}
