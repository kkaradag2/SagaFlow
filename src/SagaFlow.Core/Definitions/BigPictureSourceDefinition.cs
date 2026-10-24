namespace SagaFlow.Core.Definitions;

/// <summary>A visibility reference. It never transfers process execution ownership.</summary>
public sealed class BigPictureSourceDefinition
{
    public string Domain { get; init; } = string.Empty;
    public string Process { get; init; } = string.Empty;
    public string? Version { get; init; }
    public string Endpoint { get; init; } = string.Empty;
}
