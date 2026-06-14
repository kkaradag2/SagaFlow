namespace SagaFlow.Core.Definitions;

public sealed class SagaTransitionDefinition
{
    public string Event { get; init; } = string.Empty;

    public string? NextStep { get; init; }
}
