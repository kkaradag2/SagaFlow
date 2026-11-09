using Microsoft.Extensions.Configuration;
using SagaFlow.Engine.Discovery;
using SagaFlow.Hosting.Definitions;

namespace SagaFlow.Hosting.DependencyInjection;

/// <summary>Resolves the host configuration once and validates publication and discovery before startup.</summary>
internal sealed record SagaFlowRegistration(
    SagaFlowOptions Options,
    DefinitionPublicationSettings Publication,
    DefinitionDiscovery Discovery)
{
    internal static SagaFlowRegistration Create(
        IConfiguration? configuration, SagaFlowOptions options, IRemoteDefinitionClient client)
    {
        var section = configuration?.GetSection("SagaFlow");
        if (section is not null)
        {
            section.GetSection("Publication").Bind(options.Publication, binder => binder.ErrorOnUnknownConfiguration = true);
            section.GetSection("Discovery").Bind(options.Discovery, binder => binder.ErrorOnUnknownConfiguration = true);
        }
        if (section?["DefinitionPath"] is { } path) options.LoadConfiguration(path);

        var publication = new DefinitionPublicationSettings(options.Publication.Enabled, options.Publication.IncludeBigPictureSources);
        return new SagaFlowRegistration(options, publication, new DefinitionDiscovery(client, options.Discovery));
    }
}
