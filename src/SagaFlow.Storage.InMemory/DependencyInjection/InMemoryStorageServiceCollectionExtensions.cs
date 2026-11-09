using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SagaFlow.Core.Abstractions;

namespace SagaFlow.Storage.InMemory.DependencyInjection;

/// <summary>Registers the in-memory SagaFlow storage provider.</summary>
public static class InMemoryStorageServiceCollectionExtensions
{
    /// <summary>Selects the process-local singleton saga state store.</summary>
    public static IServiceCollection AddSagaFlowInMemoryStorage(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISagaStateStore, InMemorySagaStateStore>());
        return services;
    }
}
