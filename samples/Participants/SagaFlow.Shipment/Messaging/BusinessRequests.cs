namespace SagaFlow.Shipment.Messaging;

public sealed record CreateShipmentRequest(Guid Id, string CustomerId);
public sealed record CancelShipmentRequest(Guid Id);
