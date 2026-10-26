namespace SagaFlow.Inventory.Domain;

public sealed record InventoryLine(string ProductId, int Quantity);
public enum ReservationStatus { Reserved, Released }
public sealed record InventoryReservation(string OrderId, IReadOnlyList<InventoryLine> Lines, ReservationStatus Status);
