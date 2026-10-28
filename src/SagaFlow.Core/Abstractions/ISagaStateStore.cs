using SagaFlow.Core.State;

namespace SagaFlow.Core.Abstractions;

public interface ISagaStateStore
{
    /// <summary>Create at revision zero, or replace using an exact current revision. Never silently overwrite stale state.</summary>
    ValueTask SaveAsync(SagaInstance instance, CancellationToken cancellationToken = default);

    ValueTask<SagaInstance?> FindAsync(string instanceId, CancellationToken cancellationToken = default);

    /// <summary>Return isolated snapshots of all Saga instances currently held by the store.</summary>
    ValueTask<IReadOnlyList<SagaInstance>> ListAsync(CancellationToken cancellationToken = default);

    ValueTask<SagaInstance?> FindAsync(Guid instanceId, CancellationToken cancellationToken = default) =>
        FindAsync(instanceId.ToString("N"), cancellationToken);

    /// <summary>Atomically apply a synchronous, side-effect-free mutation to an isolated working copy.
    /// An exception or cancellation must leave stored state unchanged. Do not perform I/O in the callback.</summary>
    ValueTask<TResult> UpdateAsync<TResult>(string instanceId, Func<SagaInstance, TResult> update, CancellationToken cancellationToken = default);
}
