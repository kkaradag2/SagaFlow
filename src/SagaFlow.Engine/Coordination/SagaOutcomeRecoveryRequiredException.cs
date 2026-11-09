namespace SagaFlow.Engine.Coordination;

/// <summary>The outcome needs preservation because execution cannot safely continue from the available state.</summary>
public sealed class SagaOutcomeRecoveryRequiredException(string message) : Exception(message)
{
    public bool IsConflictingEvidence { get; init; }
}
