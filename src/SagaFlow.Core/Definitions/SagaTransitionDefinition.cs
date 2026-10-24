namespace SagaFlow.Core.Definitions;

public sealed class SagaTransitionDefinition
{
    public string Event { get; init; } = string.Empty;

    public string? When { get; init; }

    public bool? Else { get; init; }

    public string? NextStep { get; init; }
}
