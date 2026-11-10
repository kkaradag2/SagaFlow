using SagaFlow.Core.Messages;
namespace SagaFlow.Core.Abstractions;

/// <summary>Domain-to-domain transport, separate from participant commands. Completion means transport publish completed only.</summary>
public interface IProcessInvocationBus
{
    /// <summary>Completes transport routing without performing I/O. Called before the state and message commit.</summary>
    OutboxMessage PrepareOutbox(OutboxMessage message) => message;

    /// <summary>Completes successfully only after documented durable transport acceptance.</summary>
    ValueTask PublishOutboxAsync(OutboxMessage message, CancellationToken cancellationToken = default);

    ValueTask PublishAsync(ProcessInvocationRequest request, CancellationToken cancellationToken = default);
    ValueTask PublishAsync(ProcessInvocationAccepted accepted, CancellationToken cancellationToken = default);
    ValueTask PublishAsync(ProcessInvocationRejected rejected, CancellationToken cancellationToken = default);
    ValueTask PublishAsync(ProcessInvocationTerminalOutcome outcome, CancellationToken cancellationToken = default);
}
