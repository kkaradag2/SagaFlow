using SagaFlow.Inventory.Domain;

namespace SagaFlow.Inventory.Messaging;

public sealed record ReserveInventoryRequest(Guid Id, InventoryLine[] Items);
public sealed record ReleaseInventoryRequest(Guid Id);
public sealed record RestockReturnRequest(Guid ReturnId, Guid OrderId, InventoryLine[] Items);
