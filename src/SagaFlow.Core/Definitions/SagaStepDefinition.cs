namespace SagaFlow.Core.Definitions;

public sealed class SagaStepDefinition
{
    public string Name { get; init; } = string.Empty;

    public string Command { get; init; } = string.Empty;

    public string? TimeoutLimit { get; init; }

    public int MaxAttempts { get; init; } = 1;

    public IReadOnlyList<SagaTransitionDefinition> Transitions { get; init; } = [];

    public SagaCompensationDefinition? Compensation { get; init; }
}
