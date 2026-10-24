using Microsoft.Extensions.Hosting;
using SagaFlow.Engine.ProcessDefinition;
using SagaFlow.Hosting.DependencyInjection;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Definitions;

namespace SagaFlow.Hosting.Definitions;

internal sealed class DefinitionLoadingService(
    SagaFlowOptions options, IHostEnvironment environment, ProcessDefinitionRegistry registry,
    DefinitionPublicationSettings publication, DefinitionEndpointMapping mapping, ISagaDefinitionLoader loader) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (publication.Enabled && !mapping.IsMapped)
            throw new InvalidOperationException("Definition publication is enabled; call MapSagaFlowDefinitionDiscovery before starting the host.");
        if (options.DefinitionDirectory is not null)
        {
            var definitions = new List<SagaDefinition>();
            foreach (var path in YamlSagaDefinitionLoader.EnumerateDefinitionFiles(Path.GetFullPath(options.DefinitionDirectory, environment.ContentRootPath)))
                definitions.Add(await loader.LoadAsync(path, cancellationToken));
            registry.RegisterLocalBatch(definitions);
        }
        else if (options.DefinitionPath is not null)
            registry.RegisterLocalBatch([await loader.LoadAsync(Path.GetFullPath(options.DefinitionPath, environment.ContentRootPath), cancellationToken)]);

        if (publication.Enabled)
        {
            var local = registry.Snapshot().Where(x => x.Origin == DefinitionOrigin.Local).Select(x => ProcessDefinitionId.From(x.Definition)).ToHashSet();
            foreach (var id in publication.PublishedDefinitions)
                if (!local.Contains(id)) throw new InvalidOperationException($"Published SagaFlow definition '{id}' is not loaded locally.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
