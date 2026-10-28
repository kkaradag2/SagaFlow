using Microsoft.AspNetCore.SignalR;
using SagaFlow.Engine.Notifications;

namespace SagaFlow.Hosting.Dashboard;

internal sealed class SignalRSagaStateChangeListener(IHubContext<SagaFlowHub> hub) : ISagaStateChangeListener
{
    public ValueTask OnStateChangedAsync(SagaStateChanged notification, CancellationToken cancellationToken = default) =>
        new(hub.Clients.All.SendAsync("SagaInstanceChanged", notification.InstanceId, cancellationToken));
}
