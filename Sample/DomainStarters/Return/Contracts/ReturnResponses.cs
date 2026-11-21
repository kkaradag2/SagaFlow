namespace Sample.DomainStarters.Return.Contracts;

public sealed record CreateReturnResponse(Guid ReturnId, Guid OrderId, Guid SagaInstanceId);

public sealed record ReturnStatusResponse(
    Guid ReturnId,
    Guid OrderId,
    Guid? SagaInstanceId,
    string CustomerId,
    string BusinessStatus,
    string? SagaStatus,
    string? SagaCorrelationId);
