using SagaFlow.Core.State;
using SagaFlow.Core.Messages;

namespace SagaFlow.Core.Abstractions;

/// <summary>Defines saga state persistence, atomic mutations, and incoming invocation tracking.</summary>
public interface ISagaStateStore
{
    /// <summary>Create at revision zero, or replace using an exact current revision. Never silently overwrite stale state.</summary>
    ValueTask SaveAsync(SagaInstance instance, CancellationToken cancellationToken = default);

    /// <summary>Insert a recovered target and its original incoming invocation claim in one atomic operation.
    /// Returns false when an authoritative instance with the same ID already exists.</summary>
    ValueTask<bool> TryRecoverIncomingAsync(SagaInstance instance, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Store does not support atomic incoming invocation recovery.");

    /// <summary>Finds an isolated snapshot by instance ID, or returns null if the instance is missing.</summary>
    ValueTask<SagaInstance?> FindAsync(string instanceId, CancellationToken cancellationToken = default);

    /// <summary>Return isolated snapshots of all Saga instances currently held by the store.</summary>
    ValueTask<IReadOnlyList<SagaInstance>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Finds an instance using the compact string form of its GUID.</summary>
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

    /// <summary>Finds the first recorded admission decision, or returns null when no decision exists.</summary>
    ValueTask<IncomingInvocationDecision?> FindIncomingDecisionAsync(string invocationId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Store does not support incoming admission decisions.");

    /// <summary>First decision wins atomically; duplicate request identity/payload must match, otherwise conflict.</summary>
    ValueTask<IncomingInvocationDecision> RecordIncomingDecisionAsync(IncomingInvocationDecision decision, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Store does not support incoming admission decisions.");
}
