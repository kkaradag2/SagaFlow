namespace SagaFlow.Core.State;

public sealed class SagaInstance
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string DefinitionName { get; init; } = string.Empty;

    public SagaStatus Status { get; set; } = SagaStatus.Pending;

    public IReadOnlyList<SagaStepState> Steps { get; init; } = [];
}
