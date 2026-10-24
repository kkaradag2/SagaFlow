using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Messages;

namespace SagaFlow.Engine.Messaging;

public sealed class CommandDispatcher(ISagaMessageBus? messageBus = null)
{
    public void EnsureTransport() => _ = messageBus ?? throw new InvalidOperationException("No ISagaMessageBus transport adapter is registered. Register a real transport before calling ISagaFlow.StartAsync.");

    public ValueTask PublishAsync(SagaCommand command, CancellationToken cancellationToken)
    {
        EnsureTransport();
        cancellationToken.ThrowIfCancellationRequested();
        return messageBus!.PublishAsync(command, cancellationToken);
    }
}
