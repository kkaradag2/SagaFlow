using SagaFlow.Core.Messages;

namespace SagaFlow.Core.Abstractions;

public interface ISagaMessageBus
{
    string? ReplyDestination => null;
    string? CommandExchange => null;
    ValueTask PublishAsync(SagaCommand command, CancellationToken cancellationToken = default);

    /// <summary>Publishes the stored message and completes successfully only after the transport provides its documented durable acceptance confirmation.</summary>
    /// <exception cref="NotSupportedException">The transport cannot confirm durable acceptance.</exception>
    /// <remarks>Rejection, an unroutable message, and a missing or uncertain confirmation must be reported as failures.</remarks>
    ValueTask PublishOutboxAsync(OutboxMessage message, CancellationToken cancellationToken = default);
}
