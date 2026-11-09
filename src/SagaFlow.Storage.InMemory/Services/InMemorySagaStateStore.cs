using System.Collections.Concurrent;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Messages;
using SagaFlow.Core.State;
using SagaFlow.Storage.InMemory.Models;
using SagaFlow.Storage.InMemory.Services;

namespace SagaFlow.Storage.InMemory;

/// <summary>Stores saga snapshots and incoming invocation records for the lifetime of the process.</summary>
/// <remarks>Instance critical sections are synchronous. Mutation callbacks operate on isolated copies.</remarks>
public sealed class InMemorySagaStateStore : ISagaStateStore
{
    private readonly ConcurrentDictionary<string, SagaStateEntry> _instances = new(StringComparer.Ordinal);
    private readonly IncomingInvocationStore _incomingInvocations;

    /// <summary>Creates an empty store with an invocation index tied to its saga instances.</summary>
    public InMemorySagaStateStore()
    {
        _incomingInvocations = new IncomingInvocationStore(_instances);
    }

    /// <summary>Returns the recorded admission decision, or null when no decision exists.</summary>
    public ValueTask<IncomingInvocationDecision?> FindIncomingDecisionAsync(string invocationId, CancellationToken cancellationToken = default) =>
        _incomingInvocations.FindIncomingDecisionAsync(invocationId, cancellationToken);

    /// <summary>Records the first admission decision and reuses it for matching redeliveries.</summary>
    public ValueTask<IncomingInvocationDecision> RecordIncomingDecisionAsync(IncomingInvocationDecision decision, CancellationToken cancellationToken = default) =>
        _incomingInvocations.RecordIncomingDecisionAsync(decision, cancellationToken);

    /// <inheritdoc />
    public ValueTask<bool> TryRecoverIncomingAsync(SagaInstance instance, CancellationToken cancellationToken = default) =>
        _incomingInvocations.TryRecoverAsync(instance, cancellationToken);

    /// <summary>Claims an incoming invocation and creates its target on the first delivery.</summary>
    public ValueTask<SagaInstance> GetOrCreateIncomingAsync(ProcessInvocationRequest request, Func<SagaInstance> create,
        DateTimeOffset acceptedAt, CancellationToken cancellationToken = default) =>
        _incomingInvocations.GetOrCreateIncomingAsync(request, create, acceptedAt, cancellationToken);

    /// <summary>Claims an incoming invocation for an existing saga instance.</summary>
    public ValueTask<SagaInstance> AttachIncomingAsync(string targetInstanceId, ProcessInvocationRequest request,
        DateTimeOffset acceptedAt, CancellationToken cancellationToken = default) =>
        _incomingInvocations.AttachIncomingAsync(targetInstanceId, request, acceptedAt, cancellationToken);

    /// <summary>Prevents ordinary writes from bypassing the claim index or changing invocation provenance.</summary>
    private static void EnsureIncomingUnchanged(IncomingInvocation? before, IncomingInvocation? after)
    {
        if (ReferenceEquals(before, after)) return;
        if (before is null || after is null || before.Identity != after.Identity || before.CreatedAt != after.CreatedAt
            || before.AcceptedAt != after.AcceptedAt || before.Payload?.GetRawText() != after.Payload?.GetRawText()
            || before.RequestMessage != after.RequestMessage)
            throw new InvalidOperationException("Incoming provenance may only be changed by the atomic claim operation.");
    }

    /// <summary>Creates a saga at revision one or replaces it when the supplied revision matches.</summary>
    public ValueTask SaveAsync(SagaInstance instance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(instance.Id);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = SagaStateSnapshot.Copy(instance);
        var initial = new SagaStateEntry(snapshot);
        if (instance.Revision == 0)
        {
            EnsureIncomingUnchanged(null, snapshot.IncomingInvocation);
            snapshot.Revision = 1;
            if (_instances.TryAdd(instance.Id, initial)) return ValueTask.CompletedTask;
        }
        if (!_instances.TryGetValue(instance.Id, out var entry))
            throw new InvalidOperationException($"Saga '{instance.Id}' does not exist; create it with revision zero.");
        lock (entry.Gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsUpdating) throw new InvalidOperationException("Nested writes to the same Saga instance are not supported inside UpdateAsync.");
            if (instance.Revision != entry.State.Revision)
                throw new InvalidOperationException($"Stale Saga state revision for '{instance.Id}'; reload state or use UpdateAsync.");
            EnsureIncomingUnchanged(entry.State.IncomingInvocation, snapshot.IncomingInvocation);
            snapshot.Revision = entry.State.Revision + 1;
            entry.State = snapshot;
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>Returns an isolated snapshot for the instance, or null when the instance is missing.</summary>
    public ValueTask<SagaInstance?> FindAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_instances.TryGetValue(instanceId, out var entry)) return ValueTask.FromResult<SagaInstance?>(null);
        lock (entry.Gate) return ValueTask.FromResult<SagaInstance?>(SagaStateSnapshot.Copy(entry.State));
    }

    /// <summary>Returns isolated snapshots ordered by newest creation time, then by instance ID.</summary>
    public ValueTask<IReadOnlyList<SagaInstance>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshots = new List<SagaInstance>(_instances.Count);
        foreach (var entry in _instances.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (entry.Gate) snapshots.Add(SagaStateSnapshot.Copy(entry.State));
        }
        return ValueTask.FromResult<IReadOnlyList<SagaInstance>>(snapshots
            .OrderByDescending(instance => instance.CreatedAt)
            .ThenBy(instance => instance.Id, StringComparer.Ordinal)
            .ToArray());
    }

    /// <summary>Applies a synchronous mutation and commits a new revision only after it succeeds.</summary>
    /// <remarks>An exception or cancellation before commit discards the working copy.</remarks>
    public ValueTask<TResult> UpdateAsync<TResult>(string instanceId, Func<SagaInstance, TResult> update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_instances.TryGetValue(instanceId, out var entry)) throw new KeyNotFoundException($"Saga instance '{instanceId}' was not found.");
        lock (entry.Gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsUpdating) throw new InvalidOperationException("Nested writes to the same Saga instance are not supported inside UpdateAsync.");
            entry.IsUpdating = true;
            try
            {
                var working = SagaStateSnapshot.Copy(entry.State);
                var result = update(working);
                cancellationToken.ThrowIfCancellationRequested();
                EnsureIncomingUnchanged(entry.State.IncomingInvocation, working.IncomingInvocation);
                working.Revision = entry.State.Revision + 1;
                // Copy again: neither the callback nor its result may retain references into stored state.
                entry.State = SagaStateSnapshot.Copy(working);
                return ValueTask.FromResult(result);
            }
            finally { entry.IsUpdating = false; }
        }
    }
}
