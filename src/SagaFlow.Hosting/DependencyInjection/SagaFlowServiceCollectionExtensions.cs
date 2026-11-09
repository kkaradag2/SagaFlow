using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SagaFlow.Core.Abstractions;
using SagaFlow.Engine.Coordination;
using SagaFlow.Engine.ProcessDefinition;
using SagaFlow.Engine.Discovery;
using SagaFlow.Hosting.Definitions;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SagaFlow.Engine.ContextState;
using SagaFlow.Engine.Messaging;
using SagaFlow.Hosting.Dashboard;
using Microsoft.AspNetCore.Hosting;
using SagaFlow.Engine.Timeouts;
using SagaFlow.Hosting.Timeouts;
using SagaFlow.Engine.Notifications;
using SagaFlow.Engine.Consistency;

namespace SagaFlow.Hosting.DependencyInjection;

public static class SagaFlowServiceCollectionExtensions
{
    public static IServiceCollection AddSagaRollbackHandler<TPayload, THandler>(this IServiceCollection services)
        where THandler : SagaRollbackHandler<TPayload>
    {
        services.AddScoped<SagaRollbackHandler<TPayload>, THandler>();
        return services;
    }

    public static IServiceCollection AddSagaFlow(
        this IServiceCollection services,
        Action<SagaFlowOptions>? configure = null)
    {
        var options = new SagaFlowOptions();
        configure?.Invoke(options);
        services.AddSingleton<IRemoteDefinitionClient, GrpcRemoteDefinitionClient>();
        services.AddSingleton(provider => SagaFlowRegistration.Create(
            provider.GetService<IConfiguration>(), options, provider.GetRequiredService<IRemoteDefinitionClient>()));
        services.AddSingleton(provider => provider.GetRequiredService<SagaFlowRegistration>().Options);
        services.AddSingleton(provider => provider.GetRequiredService<SagaFlowRegistration>().Publication);
        services.AddSingleton(provider => provider.GetRequiredService<SagaFlowRegistration>().Discovery);
        services.TryAddSingleton<ISagaDefinitionLoader, YamlSagaDefinitionLoader>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISagaClock>(provider => new SystemSagaClock(provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<SagaTimeoutDetector>();
        services.TryAddSingleton<SagaStateChangePublisher>();
        services.AddLogging();
        services.AddSingleton<TransitionResolver>();
        services.AddSingleton<ProcessStateManager>();
        services.AddScoped<CompensationManager>();
        services.AddScoped<SagaInstanceManager>();
        services.AddScoped<CommandDispatcher>();
        services.AddScoped<ProcessInvocationManager>();
        services.AddScoped<InvocationExecutionContext>();
        services.AddSingleton<ILocalSagaExecutionBoundary, LocalSagaExecutionBoundary>();
        services.AddScoped<SagaStepExecutor>();
        services.AddScoped<SagaCoordinator>();
        services.AddScoped<ISagaFlow>(provider => provider.GetRequiredService<SagaCoordinator>());
        services.AddScoped<OutcomeEventReceiver>();
        services.AddSingleton<ProcessDefinitionRegistry>();
        services.AddSingleton<DefinitionEndpointMapping>();
        if (options.Dashboard.Enabled)
        {
            options.Dashboard.Path = SagaFlowDashboardPath.Validate(options.Dashboard.Path);
            services.AddSignalR();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<ISagaStateChangeListener, SignalRSagaStateChangeListener>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IStartupFilter, SagaFlowDashboardStartupFilter>());
        }
        services.AddHostedService<DefinitionLoadingService>();
        services.AddHostedService(provider =>
        {
            // The timeout worker requires the store at construction. Validate before DI resolves it.
            StorageProviderValidation.Validate(services, provider);
            return ActivatorUtilities.CreateInstance<SagaTimeoutDetectionService>(provider);
        });
        services.AddGrpc();
        options.Storage.Register?.Invoke(services);
        options.Messaging.Register?.Invoke(services);

        return services;
    }
}

public sealed class SagaFlowOptions
{
    public string? DefinitionPath { get; private set; }
    public string? DefinitionDirectory { get; private set; }
    public DefinitionPublicationOptions Publication { get; } = new();
    public DefinitionDiscoveryOptions Discovery { get; } = new();
    public SagaFlowStorageOptions Storage { get; } = new();
    public SagaFlowMessagingOptions Messaging { get; } = new();
    internal SagaFlowDashboardOptions Dashboard { get; } = new() { Enabled = false };

    /// <summary>Enables the read-only Dashboard and its HTTP and SignalR endpoints.</summary>
    public void UseDashboard(Action<SagaFlowDashboardOptions>? configure = null)
    {
        Dashboard.Enabled = true;
        configure?.Invoke(Dashboard);
    }

    /// <summary>Loads one Saga YAML file at startup. Relative paths resolve from the host content root.</summary>
    public void LoadConfiguration(string path) => LoadFromYaml(path);

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
