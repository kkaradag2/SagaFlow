using System.Collections.Concurrent;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.State;
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

    public ValueTask SaveAsync(SagaInstance instance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(instance.Id);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = SagaStateSnapshot.Copy(instance);
        var initial = new Entry(snapshot);
        if (instance.Revision == 0)
        {
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
                working.Revision = entry.State.Revision + 1;
                // Copy again: neither the callback nor its result may retain references into stored state.
                entry.State = SagaStateSnapshot.Copy(working);
                return ValueTask.FromResult(result);
            }
            finally { entry.IsUpdating = false; }
        }
    }
}
