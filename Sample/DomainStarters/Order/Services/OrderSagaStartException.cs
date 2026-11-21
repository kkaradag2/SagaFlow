namespace Sample.DomainStarters.Order.Services;

internal sealed class OrderSagaStartException(Guid orderId, Guid? sagaInstanceId, Exception innerException)
    : Exception("The order was saved, but SagaFlow did not confirm startup.", innerException)
{
    public Guid OrderId { get; } = orderId;
    public Guid? SagaInstanceId { get; } = sagaInstanceId;
}
