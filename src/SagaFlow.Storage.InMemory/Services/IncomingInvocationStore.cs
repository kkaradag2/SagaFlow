using System.Collections.Concurrent;
using SagaFlow.Core.Messages;
using SagaFlow.Core.State;
using SagaFlow.Storage.InMemory.Models;

namespace SagaFlow.Storage.InMemory.Services;

/// <summary>Owns incoming invocation claims and admission decisions for one in-memory store.</summary>
/// <remarks>Claims share the instance index with the store so a claim can publish target state and bind its invocation ID within the same critical section.</remarks>
internal sealed class IncomingInvocationStore(ConcurrentDictionary<string, SagaStateEntry> instances)
{
    // Acquire this gate before an entry gate when both are needed.
    private readonly object _incomingGate = new();
    private readonly Dictionary<Guid, string> _incoming = [];
    private bool _claiming;
    private readonly Dictionary<Guid, IncomingInvocationDecision> _decisions = [];

    /// <summary>Reads the recorded reply and request identity for a redelivered invocation.</summary>
    internal ValueTask<IncomingInvocationDecision?> FindIncomingDecisionAsync(string invocationId, CancellationToken cancellationToken = default)
    {
        lock (_incomingGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_decisions.GetValueOrDefault(Guid.Parse(invocationId)));
        }
    }

    /// <summary>Keeps the first decision and returns it for a matching duplicate request.</summary>
    internal ValueTask<IncomingInvocationDecision> RecordIncomingDecisionAsync(IncomingInvocationDecision decision, CancellationToken cancellationToken = default)
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
                // Claims set provenance under the incoming gate. Ordinary state writes preserve its value.
                var state = instances[target].State.IncomingInvocation!;
                if (state.Identity != decision.Request.Identity || state.Payload?.GetRawText() != decision.Request.Payload.GetRawText()
                    || decision.RejectionReason is not null) throw new InvalidOperationException("Admission conflicts with an existing target.");
            }
            _decisions.Add(key, decision);
            return ValueTask.FromResult(decision);
        }
    }

    /// <summary>Rejects reuse of an invocation ID with different identity or JSON payload.</summary>
    private static void ValidateSameRequest(ProcessInvocationRequest expected, ProcessInvocationRequest actual)
    {
        if (expected.Identity != actual.Identity || expected.Payload.GetRawText() != actual.Payload.GetRawText())
            throw new InvalidOperationException("Invocation identity/payload conflict.");
    }

    /// <summary>Creates a target for the first claim and returns its current snapshot on a matching duplicate.</summary>
    internal ValueTask<SagaInstance> GetOrCreateIncomingAsync(ProcessInvocationRequest request, Func<SagaInstance> create,
        DateTimeOffset acceptedAt, CancellationToken cancellationToken = default) => ClaimIncoming(request, create, null, acceptedAt, cancellationToken);

    /// <summary>Binds an existing target and rejects attempts to move the claim to another saga.</summary>
    internal ValueTask<SagaInstance> AttachIncomingAsync(string targetInstanceId, ProcessInvocationRequest request,
        DateTimeOffset acceptedAt, CancellationToken cancellationToken = default) => ClaimIncoming(request, null, targetInstanceId, acceptedAt, cancellationToken);

    /// <summary>Serializes claim validation, target state publication, and registration of the invocation ID.</summary>
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
                    var existing = instances[claimed];
                    lock (existing.Gate)
                    {
                        var incoming = existing.State.IncomingInvocation!;
                        if (incoming.Identity != request.Identity || incoming.Payload?.GetRawText() != request.Payload.GetRawText())
                            throw new InvalidOperationException("Invocation identity/payload conflict.");
                        cancellationToken.ThrowIfCancellationRequested();
                        return ValueTask.FromResult(SagaStateSnapshot.Copy(existing.State));
                    }
                }
                SagaStateEntry? entry = null;
                if (targetId is not null && !instances.TryGetValue(targetId, out entry)) throw new KeyNotFoundException("Target Saga not found.");
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
                        if (!instances.TryAdd(working.Id, new SagaStateEntry(working))) throw new InvalidOperationException("Target Saga ID collision.");
                    }
                    else entry.State = working;
                    _incoming.Add(key, working.Id);
                    return ValueTask.FromResult(result);
                }
            }
            finally { _claiming = false; }
        }
    }
}
