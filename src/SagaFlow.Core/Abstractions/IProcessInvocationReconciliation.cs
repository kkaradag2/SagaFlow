namespace SagaFlow.Core.Abstractions;

/// <summary>Broker-backed early terminal parking. No automatic retry loop; bounded replay is explicit or acceptance-triggered.</summary>
public interface IProcessInvocationReconciliation
{
    ValueTask<uint> GetDeferredCountAsync(CancellationToken cancellationToken = default);
    /// <summary>Inspect at most maxMessages (1..64) parked messages. Return successfully applied count; not-ready messages remain parked.</summary>
    ValueTask<int> ReconcileDeferredAsync(int maxMessages = 64, CancellationToken cancellationToken = default);
}
