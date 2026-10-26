namespace SagaFlow.Order.Contracts.Responses;

public sealed record SuccessResponse<T>(T Data);
