using System.Collections.Concurrent;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.State;
using SagaFlow.Core.Messages;
using SagaFlow.Engine.ContextState;

namespace SagaFlow.Engine.Persistence.InMemory;

/// <summary>Process-local state only. Per-instance critical sections contain no network calls or awaits.</summary>
public sealed class InMemorySagaStateStore : ISagaStateStore
{
    private sealed class Entry(SagaInstance state)
    {
        public object Gate { get; } = new();
        public SagaInstance State { get; set; } = state;
        public bool IsUpdating { get; set; }
    }
    private readonly ConcurrentDictionary<string, Entry> _instances = new(StringComparer.Ordinal);

    private readonly object _incomingGate = new();
    private readonly Dictionary<Guid, string> _incoming = [];
    private bool _claiming;
    private readonly Dictionary<Guid, IncomingInvocationDecision> _decisions = [];

    public ValueTask<IncomingInvocationDecision?> FindIncomingDecisionAsync(string invocationId, CancellationToken cancellationToken = default)
    {
        lock (_incomingGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_decisions.GetValueOrDefault(Guid.Parse(invocationId)));
        }
    }

    public ValueTask<IncomingInvocationDecision> RecordIncomingDecisionAsync(IncomingInvocationDecision decision, CancellationToken cancellationToken = default)
    {
        decision.Reply.ValidateAgainst(decision.Request.Identity);
        lock (_incomingGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Guid.Parse(decision.Request.Identity.InvocationId);
            if (_decisions.TryGetValue(key, out var existing))
            {
                ValidateSameRequest(existing.Request, decision.Request);
                return ValueTask.FromResult(existing);
            }
            if (_incoming.TryGetValue(key, out var target))
            {
                var state = _instances[target].State.IncomingInvocation!;
                if (state.Identity != decision.Request.Identity || state.Payload?.GetRawText() != decision.Request.Payload.GetRawText()
                    || decision.RejectionReason is not null) throw new InvalidOperationException("Admission conflicts with an existing target.");
            }
            _decisions.Add(key, decision);
            return ValueTask.FromResult(decision);
        }
    }

    private static void ValidateSameRequest(ProcessInvocationRequest expected, ProcessInvocationRequest actual)
    {
        if (expected.Identity != actual.Identity || expected.Payload.GetRawText() != actual.Payload.GetRawText())
            throw new InvalidOperationException("Invocation identity/payload conflict.");
    }

    public ValueTask<SagaInstance> GetOrCreateIncomingAsync(ProcessInvocationRequest request, Func<SagaInstance> create,
        DateTimeOffset acceptedAt, CancellationToken cancellationToken = default) => ClaimIncoming(request, create, null, acceptedAt, cancellationToken);

    public ValueTask<SagaInstance> AttachIncomingAsync(string targetInstanceId, ProcessInvocationRequest request,
        DateTimeOffset acceptedAt, CancellationToken cancellationToken = default) => ClaimIncoming(request, null, targetInstanceId, acceptedAt, cancellationToken);

    private ValueTask<SagaInstance> ClaimIncoming(ProcessInvocationRequest request, Func<SagaInstance>? create,
        string? targetId, DateTimeOffset acceptedAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (acceptedAt == default) throw new ArgumentException("Acceptance time required.");
        cancellationToken.ThrowIfCancellationRequested();
        lock (_incomingGate)
        {
            if (_claiming) throw new InvalidOperationException("Nested invocation claims are unsupported.");
            _claiming = true;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = Guid.Parse(request.Identity.InvocationId);
                if (_decisions.TryGetValue(key, out var decision))
                {
                    ValidateSameRequest(decision.Request, request);
                    if (decision.RejectionReason is not null) throw new InvalidOperationException("Invocation was rejected.");
                }
                if (_incoming.TryGetValue(key, out var claimed))
                {
                    if (targetId is not null && targetId != claimed) throw new InvalidOperationException("Invocation already belongs to another target Saga.");
                    var existing = _instances[claimed];
                    lock (existing.Gate)
                    {
                        var incoming = existing.State.IncomingInvocation!;
                        if (incoming.Identity != request.Identity || incoming.Payload?.GetRawText() != request.Payload.GetRawText())
                            throw new InvalidOperationException("Invocation identity/payload conflict.");
                        cancellationToken.ThrowIfCancellationRequested();
                        return ValueTask.FromResult(SagaStateSnapshot.Copy(existing.State));
                    }
                }
                Entry? entry = null;
                if (targetId is not null && !_instances.TryGetValue(targetId, out entry)) throw new KeyNotFoundException("Target Saga not found.");
                // No I/O is allowed in the factory. All potentially throwing preparation precedes either commit.
                var candidate = targetId is null ? create!() : null;
                var gate = entry?.Gate ?? new object();
                lock (gate)
                {
                    if (entry?.IsUpdating == true) throw new InvalidOperationException("Nested target mutation is unsupported.");
                    var working = SagaStateSnapshot.Copy(entry?.State ?? candidate!);
                    if (string.IsNullOrWhiteSpace(working.Id) || working.Status != SagaStatus.Running || working.IncomingInvocation is not null
                        || new ProcessIdentity(working.DefinitionDomain, working.DefinitionName, working.DefinitionVersion) != request.Identity.Target)
                        throw new InvalidOperationException("Invalid target state/process identity for incoming invocation.");
                    if (targetId is null && working.Revision != 0) throw new InvalidOperationException("Factory must return a new unsaved instance.");
                    working.IncomingInvocation = new(request.Identity, request.Message.OccurredAt, acceptedAt.ToUniversalTime(), request.Payload.Clone());
                    working.UpdatedAt = acceptedAt.ToUniversalTime();
                    working.Revision = (entry?.State.Revision ?? 0) + 1;
                    var result = SagaStateSnapshot.Copy(working);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry is null)
                    {
                        if (!_instances.TryAdd(working.Id, new Entry(working))) throw new InvalidOperationException("Target Saga ID collision.");
                    }
                    else entry.State = working;
                    _incoming.Add(key, working.Id);
                    return ValueTask.FromResult(result);
                }
            }
            finally { _claiming = false; }
        }
    }

    private static void EnsureIncomingUnchanged(IncomingInvocation? before, IncomingInvocation? after)
    {
        if (ReferenceEquals(before, after)) return;
        if (before is null || after is null || before.Identity != after.Identity || before.CreatedAt != after.CreatedAt
            || before.AcceptedAt != after.AcceptedAt || before.Payload?.GetRawText() != after.Payload?.GetRawText())
            throw new InvalidOperationException("Incoming provenance may only be changed by the atomic claim operation.");
    }

    public ValueTask SaveAsync(SagaInstance instance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(instance.Id);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = SagaStateSnapshot.Copy(instance);
        var initial = new Entry(snapshot);
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

    public ValueTask<SagaInstance?> FindAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_instances.TryGetValue(instanceId, out var entry)) return ValueTask.FromResult<SagaInstance?>(null);
        lock (entry.Gate) return ValueTask.FromResult<SagaInstance?>(SagaStateSnapshot.Copy(entry.State));
    }

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
