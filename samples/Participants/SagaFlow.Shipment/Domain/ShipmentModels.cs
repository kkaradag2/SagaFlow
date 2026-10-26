namespace SagaFlow.Shipment.Domain;

public enum ShipmentStatus { Created, Cancelled }
public sealed record ShipmentRecord(string OrderId, string Address, string TrackingNumber, ShipmentStatus Status);
