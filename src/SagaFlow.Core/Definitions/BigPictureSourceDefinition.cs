namespace SagaFlow.Core.Definitions;


/// <summary>A definition reference. "invokes" declares an allowed invocation target; execution ownership remains remote.</summary>
public sealed class BigPictureSourceDefinition
{
    public string Domain { get; init; } = string.Empty;
    public string Process { get; init; } = string.Empty;
    public string? Version { get; init; }
    public string Endpoint { get; init; } = string.Empty;
    private string? _relationship;
    public string? Relationship
    {
        get => _relationship;
        init { _relationship = value; _relationshipSpecified = true; }
    }
    private bool _relationshipSpecified;
    public bool HasExplicitRelationship() => _relationshipSpecified;

    public BigPictureSourceDefinition Snapshot() => _relationshipSpecified
        ? new() { Domain = Domain, Process = Process, Version = Version, Endpoint = Endpoint, Relationship = Relationship }
        : new() { Domain = Domain, Process = Process, Version = Version, Endpoint = Endpoint };
}
