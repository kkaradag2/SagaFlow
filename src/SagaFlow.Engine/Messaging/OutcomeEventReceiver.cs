using SagaFlow.Core.Messages;
using SagaFlow.Engine.Coordination;

namespace SagaFlow.Engine.Messaging;

public sealed class OutcomeEventReceiver(SagaCoordinator coordinator)
{
    public Task ReceiveAsync(SagaOutcomeEvent outcome, CancellationToken cancellationToken = default) => coordinator.HandleOutcomeAsync(outcome, cancellationToken);
}
