namespace SagaFlow.Core.Definitions;

public sealed class SagaDefinition
{
    public string Name { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public string Domain { get; init; } = string.Empty;

    public IReadOnlyList<SagaStepDefinition> Steps { get; init; } = [];

    public IReadOnlyList<BigPictureSourceDefinition> BigPicture { get; init; } = [];
}
