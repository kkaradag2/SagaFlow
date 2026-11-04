using SagaFlow.Core.State;
using SagaFlow.Core.Messages;

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

    /// <summary>Atomically claim InvocationId and create/attach one target instance. Duplicate identity/payload returns
    /// the existing instance without calling create. The synchronous factory must perform no I/O or store calls.
    /// Adapters must commit the unique claim, instance and incoming relationship in one transaction, or change nothing.</summary>
    ValueTask<SagaInstance> GetOrCreateIncomingAsync(ProcessInvocationRequest request, Func<SagaInstance> create,
        DateTimeOffset acceptedAt, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Store does not support atomic incoming invocation claims.");

    /// <summary>Atomically claim InvocationId for an existing instance. Reject binding it to another target.</summary>
    ValueTask<SagaInstance> AttachIncomingAsync(string targetInstanceId, ProcessInvocationRequest request,
        DateTimeOffset acceptedAt, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Store does not support atomic incoming invocation claims.");
}
