namespace SagaFlow.Order.Contracts.Responses;

public sealed record ErrorResponse(
    string Code,
    string Message,
    IReadOnlyList<ErrorDetail> Errors,
    string TraceId);
