namespace Sample.DomainStarters.Return.Services;

internal sealed class ReturnSagaStartException(Guid returnId, Guid? sagaInstanceId, Exception innerException)
    : Exception("Return saved, but Saga startup could not be confirmed.", innerException)
{
    public Guid ReturnId { get; } = returnId;
    public Guid? SagaInstanceId { get; } = sagaInstanceId;
}
