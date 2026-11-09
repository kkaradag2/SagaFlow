using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SagaFlow.Core.Abstractions;
using SagaFlow.Hosting.DependencyInjection;

namespace SagaFlow.Storage.InMemory.DependencyInjection;

/// <summary>Registers the in-memory SagaFlow storage provider.</summary>
public static class InMemoryStorageServiceCollectionExtensions
{
    /// <summary>Selects the process-local singleton store for this SagaFlow host.</summary>
    public static void UseInMemory(this SagaFlowStorageOptions storage) =>
        storage.Select(nameof(UseInMemory), services => services.AddSingleton<ISagaStateStore, InMemorySagaStateStore>());

    /// <summary>Selects the process-local singleton saga state store.</summary>
    public static IServiceCollection AddSagaFlowInMemoryStorage(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISagaStateStore, InMemorySagaStateStore>());
        return services;
    }
}
