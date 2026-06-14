namespace SagaFlow.Core.Definitions;

public sealed class SagaStepDefinition
{
    public string Name { get; init; } = string.Empty;

    public string Command { get; init; } = string.Empty;

    public SagaCompensationDefinition? Compensation { get; init; }
}
