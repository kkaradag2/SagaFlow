using SagaFlow.Core.Messages;

namespace SagaFlow.Core.Abstractions;

public interface ISagaMessageBus
{
    ValueTask PublishAsync(SagaCommand command, CancellationToken cancellationToken = default);
}
