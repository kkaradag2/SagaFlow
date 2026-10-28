using Microsoft.Extensions.DependencyInjection;
using SagaFlow.Core.Abstractions;
using SagaFlow.Engine.Coordination;
using SagaFlow.Engine.ProcessDefinition;
using SagaFlow.Engine.Discovery;
using SagaFlow.Hosting.Definitions;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SagaFlow.Engine.ContextState;
using SagaFlow.Engine.Messaging;
using SagaFlow.Engine.Persistence.InMemory;
using SagaFlow.Hosting.Dashboard;
using Microsoft.AspNetCore.Hosting;

namespace SagaFlow.Hosting.DependencyInjection;

public static class SagaFlowServiceCollectionExtensions
{
    public static IServiceCollection AddSagaFlow(
        this IServiceCollection services,
        Action<SagaFlowOptions>? configure = null)
    {
        var options = new SagaFlowOptions();
        configure?.Invoke(options);

        if (options.Publication.Enabled && options.Publication.PublishedDefinitions.Count == 0)
            throw new InvalidOperationException("Definition publication requires an explicit nonempty PublishedDefinitions allowlist.");
        var publication = new DefinitionPublicationSettings(options.Publication.Enabled,
            options.Publication.PublishedDefinitions.ToHashSet(), options.Publication.IncludeBigPictureSources);
        // Validate and freeze the outbound trust policy even when no request has been made.
        var client = new GrpcRemoteDefinitionClient();
        var discovery = new DefinitionDiscovery(client, options.Discovery);

        services.AddSingleton(options);
        services.TryAddSingleton<ISagaDefinitionLoader, YamlSagaDefinitionLoader>();
        services.TryAddSingleton<ISagaStateStore, InMemorySagaStateStore>();
        services.TryAddSingleton<ISagaClock, SystemSagaClock>();
        services.AddSingleton<TransitionResolver>();
        services.AddSingleton<ProcessStateManager>();
        services.AddScoped<SagaInstanceManager>();
        services.AddScoped<CommandDispatcher>();
        services.AddScoped<SagaStepExecutor>();
        services.AddScoped<SagaCoordinator>();
        services.AddScoped<ISagaFlow>(provider => provider.GetRequiredService<SagaCoordinator>());
        services.AddScoped<OutcomeEventReceiver>();
        services.AddSingleton<ProcessDefinitionRegistry>();
        services.AddSingleton(publication);
        services.AddSingleton<DefinitionEndpointMapping>();
        services.AddSingleton<IRemoteDefinitionClient>(client);
        services.AddSingleton(discovery);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IStartupFilter, SagaFlowDashboardStartupFilter>());
        services.AddHostedService<DefinitionLoadingService>();
        if (publication.Enabled) services.AddGrpc();

        return services;
    }
}

public sealed class SagaFlowOptions
{
    public string? DefinitionPath { get; private set; }
    public string? DefinitionDirectory { get; private set; }
    public DefinitionPublicationOptions Publication { get; } = new();
    public DefinitionDiscoveryOptions Discovery { get; } = new();
    public SagaFlowDashboardOptions Dashboard { get; } = new();

    public void LoadFromYaml(string path)
    {
        if (DefinitionDirectory is not null) throw new InvalidOperationException("Choose one definition source: file or directory.");
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        DefinitionPath = path;
    }

    public void LoadFromDirectory(string directory)
    {
        if (DefinitionPath is not null) throw new InvalidOperationException("Choose one definition source: file or directory.");
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        DefinitionDirectory = directory;
    }
}
