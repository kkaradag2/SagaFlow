using Microsoft.Extensions.DependencyInjection;
using SagaFlow.Core.Abstractions;
using SagaFlow.Engine.Coordination;
using SagaFlow.Engine.ProcessDefinition;

namespace SagaFlow.Hosting.DependencyInjection;

public static class SagaFlowServiceCollectionExtensions
{
    public static IServiceCollection AddSagaFlow(
        this IServiceCollection services,
        Action<SagaFlowOptions>? configure = null)
    {
        var options = new SagaFlowOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddSingleton<ISagaDefinitionLoader, YamlSagaDefinitionLoader>();
        services.AddSingleton<SagaCoordinator>();

        return services;
    }
}

public sealed class SagaFlowOptions
{
    public string? DefinitionPath { get; private set; }

    public void LoadFromYaml(string path)
    {
        DefinitionPath = path;
    }
}
