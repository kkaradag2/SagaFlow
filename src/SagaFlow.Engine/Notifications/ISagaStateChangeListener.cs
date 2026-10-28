namespace SagaFlow.Engine.Notifications;

public interface ISagaStateChangeListener
{
    ValueTask OnStateChangedAsync(SagaStateChanged notification, CancellationToken cancellationToken = default);
}
