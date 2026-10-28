namespace SagaFlow.Engine.Notifications;

/// <summary>Best-effort, process-local notification fan-out after durable state mutations.</summary>
public sealed class SagaStateChangePublisher(IEnumerable<ISagaStateChangeListener>? listeners = null)
{
    private readonly ISagaStateChangeListener[] _listeners = listeners?.ToArray() ?? [];

    public async ValueTask PublishAsync(string instanceId)
    {
        var notification = new SagaStateChanged(instanceId);
        foreach (var listener in _listeners)
        {
            try { await listener.OnStateChangedAsync(notification, CancellationToken.None); }
            catch { /* Visibility must never change Saga execution semantics. */ }
        }
    }
}
