using Microsoft.Extensions.Hosting;
using SagaFlow.Engine.ProcessDefinition;
using SagaFlow.Hosting.DependencyInjection;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Definitions;

namespace SagaFlow.Hosting.Definitions;

internal sealed class DefinitionLoadingService(
    SagaFlowOptions options, IHostEnvironment environment, ProcessDefinitionRegistry registry,
    DefinitionPublicationSettings publication, DefinitionEndpointMapping mapping, ISagaDefinitionLoader loader) : IHostedLifecycleService
{
    public async Task StartingAsync(CancellationToken cancellationToken)
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
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
