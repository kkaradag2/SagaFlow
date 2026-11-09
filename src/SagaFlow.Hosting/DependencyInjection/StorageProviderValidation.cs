using Microsoft.Extensions.DependencyInjection;
using SagaFlow.Core.Abstractions;

namespace SagaFlow.Hosting.DependencyInjection;

/// <summary>Checks the selected store before SagaFlow's hosted services start.</summary>
internal static class StorageProviderValidation
{
    internal static void Validate(IServiceCollection services, IServiceProvider provider)
    {
        var registrations = services
            .Where(service => service.ServiceType == typeof(ISagaStateStore) && !service.IsKeyedService)
            .ToArray();

        if (registrations.Length == 0)
            throw new InvalidOperationException("No SagaFlow storage provider is registered. Call your storage provider's registration extension or use AddSingleton<ISagaStateStore> before starting the host.");
        if (registrations.Length > 1)
            throw new InvalidOperationException("Multiple SagaFlow storage providers are registered. Remove the extra unkeyed ISagaStateStore registrations and keep exactly one.");
        if (registrations[0].Lifetime != ServiceLifetime.Singleton)
            throw new InvalidOperationException("The SagaFlow storage provider must be a singleton. Register ISagaStateStore with AddSingleton or use a storage provider's registration extension.");

        ISagaStateStore? store;
        try
        {
            store = provider.GetService<ISagaStateStore>();
        }
        catch (Exception error)
        {
            throw new InvalidOperationException("SagaFlow could not create the registered ISagaStateStore. Check the storage provider factory, dependencies, and configuration.", error);
        }

        if (store is null)
            throw new InvalidOperationException("The registered SagaFlow storage provider returned null. Configure the singleton ISagaStateStore registration to return a store instance.");
    }
}
