namespace SagaFlow.Hosting.Definitions;

public sealed class DefinitionPublicationOptions
{
    public bool Enabled { get; set; }
    /// <summary>Includes YAML references so clients can traverse beyond the published process.</summary>
    public bool IncludeBigPictureSources { get; set; }
}

internal sealed record DefinitionPublicationSettings(bool Enabled, bool IncludeBigPictureSources);

internal sealed class DefinitionEndpointMapping
{
    public bool IsMapped { get; set; }
}
