namespace SagaFlow.Core.Definitions;

public sealed class SagaStepDefinition
{
    public string Name { get; init; } = string.Empty;

    /// <summary>Wait for an invoked process instead of publishing a participant command. Default preserves legacy steps.</summary>
    public bool External { get; init; }

    public string Command { get; init; } = string.Empty;

    public string? TimeoutLimit { get; init; }

    public int MaxAttempts { get; init; } = 1;

    public IReadOnlyList<SagaTransitionDefinition> Transitions { get; init; } = [];

    public SagaCompensationDefinition? Compensation { get; init; }
}
