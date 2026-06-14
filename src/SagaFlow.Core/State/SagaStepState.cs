namespace SagaFlow.Core.State;

public sealed class SagaStepState
{
    public string StepName { get; init; } = string.Empty;

    public SagaStatus Status { get; set; } = SagaStatus.Pending;
}
