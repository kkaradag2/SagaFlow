using Microsoft.Extensions.Logging;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Messages;

namespace SagaFlow.Engine.Messaging;

public sealed class CommandDispatcher(ILogger<CommandDispatcher>? logger, ISagaMessageBus? messageBus, IProcessInvocationBus? invocationBus = null)
{
    public CommandDispatcher(ISagaMessageBus? messageBus = null, IProcessInvocationBus? invocationBus = null)
        : this(null, messageBus, invocationBus) { }

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
            catch (Exception error)
            {
                // Confirmation or bookkeeping can be uncertain. Retain the original message for retry.
                try
                {
                    var seconds = Math.Min(60, Math.Pow(2, Math.Min(claim.Attempts, 6)));
                    await store.ReleaseOutboxAsync(claim.Message.MessageId, owner,
                        DateTimeOffset.UtcNow.AddSeconds(seconds), CancellationToken.None);
                    var message = claim.Message;
                    var invocation = message.Invocation;
                    var sagaId = invocation is { IsRequest: false } ? invocation.TargetSagaInstanceId : message.SagaInstanceId;
                    var stepId = invocation is null ? message.Headers.StepId : invocation.IsRequest ? invocation.Message.Identity.SourceStep : null;
                    var correlationId = invocation?.Message.Identity.CorrelationId ?? message.Headers.CorrelationId;
                    logger?.LogWarning("Outbox message {MessageId} for Saga {SagaId} step {StepId} with correlation {CorrelationId} will be retried in {RetryDelaySeconds} seconds after attempt {Attempt}. Error type: {ErrorType}.",
                        message.MessageId, sagaId, stepId, correlationId,
                        seconds, claim.Attempts, error.GetType().Name);
                }
                catch (OutboxClaimLostException) { /* Lease was reclaimed; the next owner will retry. */ }
            }
        }
    }
}
