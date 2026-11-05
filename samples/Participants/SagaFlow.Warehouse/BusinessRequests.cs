namespace SagaFlow.Warehouse;

public sealed record InspectReturnRequest(Guid ReturnId, Guid OrderId);
