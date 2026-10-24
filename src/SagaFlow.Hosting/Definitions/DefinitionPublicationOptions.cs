using SagaFlow.Engine.ProcessDefinition;

namespace SagaFlow.Hosting.Definitions;

public sealed class DefinitionPublicationOptions
{
    public bool Enabled { get; set; }
    public ISet<ProcessDefinitionId> PublishedDefinitions { get; } = new HashSet<ProcessDefinitionId>();
    /// <summary>Opt in only when referenced endpoint origins are safe to disclose as public topology.</summary>
    public bool IncludeBigPictureSources { get; set; }
}

internal sealed record DefinitionPublicationSettings(bool Enabled, IReadOnlySet<ProcessDefinitionId> PublishedDefinitions, bool IncludeBigPictureSources);

internal sealed class DefinitionEndpointMapping
{
    public bool IsMapped { get; set; }
}
