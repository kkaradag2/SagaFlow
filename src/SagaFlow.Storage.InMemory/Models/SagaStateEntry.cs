using SagaFlow.Core.State;

namespace SagaFlow.Storage.InMemory.Models;

/// <summary>Holds the current snapshot and synchronization state for one stored saga instance.</summary>
internal sealed class SagaStateEntry(SagaInstance state)
{
    /// <summary>Serializes instance snapshot reads and writes.</summary>
    public object Gate { get; } = new();

    /// <summary>The current committed snapshot. Writers replace this reference while holding the gate.</summary>
    public SagaInstance State { get; set; } = state;

    /// <summary>Detects reentrant writes from a mutation callback. Read and write this flag while holding the gate.</summary>
    public bool IsUpdating { get; set; }
}
