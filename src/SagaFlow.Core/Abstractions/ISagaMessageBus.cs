using SagaFlow.Core.Messages;

namespace SagaFlow.Core.Abstractions;

public interface ISagaMessageBus
{
    string? ReplyDestination => null;
    ValueTask PublishAsync(SagaCommand command, CancellationToken cancellationToken = default);
}
