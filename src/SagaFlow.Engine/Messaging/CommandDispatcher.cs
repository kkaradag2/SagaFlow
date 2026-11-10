using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Messages;

namespace SagaFlow.Engine.Messaging;

public sealed class CommandDispatcher(ISagaMessageBus? messageBus = null, IProcessInvocationBus? invocationBus = null)
{
    public string? ReplyDestination => messageBus?.ReplyDestination;
    public string? CommandExchange => messageBus?.CommandExchange;
    public void EnsureTransport() => _ = messageBus ?? throw new InvalidOperationException("No ISagaMessageBus transport adapter is registered. Register a real transport before calling ISagaFlow.StartAsync.");

    public ValueTask PublishAsync(SagaCommand command, CancellationToken cancellationToken)
    {
        EnsureTransport();
        cancellationToken.ThrowIfCancellationRequested();
        return messageBus!.PublishAsync(command, cancellationToken);
    }

    /// <summary>One bounded dispatch pass. A failed broker attempt leaves a durable pending entry.</summary>
    public async Task DispatchOutboxAsync(ISagaStateStore store, CancellationToken cancellationToken, int limit = 16)
    {
        var owner = Guid.NewGuid().ToString("N");
        var claims = await store.ClaimOutboxAsync(owner, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2), limit, cancellationToken);
        foreach (var claim in claims)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (claim.Message.Invocation is null)
                    await (messageBus ?? throw new NotSupportedException("Participant transport is unavailable."))
                        .PublishOutboxAsync(claim.Message, cancellationToken);
                else
                    await (invocationBus ?? throw new NotSupportedException("Invocation transport is unavailable."))
                        .PublishOutboxAsync(claim.Message, cancellationToken);
                await store.MarkOutboxPublishedAsync(claim.Message.MessageId, owner, DateTimeOffset.UtcNow, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                // Confirmation or bookkeeping can be uncertain. Retain the original message for retry.
                try
                {
                    var seconds = Math.Min(60, Math.Pow(2, Math.Min(claim.Attempts, 6)));
                    await store.ReleaseOutboxAsync(claim.Message.MessageId, owner,
                        DateTimeOffset.UtcNow.AddSeconds(seconds), CancellationToken.None);
                }
                catch (OutboxClaimLostException) { /* Lease was reclaimed; the next owner will retry. */ }
            }
        }
    }
}
