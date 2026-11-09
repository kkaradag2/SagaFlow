using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SagaFlow.Core.Abstractions;
using SagaFlow.Hosting.DependencyInjection;
using SagaFlow.Storage.InMemory;
using SagaFlow.Storage.InMemory.DependencyInjection;

namespace SagaFlow.Hosting.Tests;

public sealed class StorageProviderRegistrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitInMemoryStartsInEitherOrderAndSharesOneSingleton(bool storageFirst)
    {
        var builder = CreateBuilder();
        if (storageFirst) builder.Services.AddSagaFlowInMemoryStorage();
        builder.Services.AddSagaFlow();
        if (!storageFirst) builder.Services.AddSagaFlowInMemoryStorage();
        builder.Services.AddSagaFlowInMemoryStorage();

        using var host = builder.Build();
        await host.StartAsync();
        using var first = host.Services.CreateScope();
        using var second = host.Services.CreateScope();
        var store = Assert.IsType<InMemorySagaStateStore>(host.Services.GetRequiredService<ISagaStateStore>());
        Assert.Same(store, first.ServiceProvider.GetRequiredService<ISagaStateStore>());
        Assert.Same(store, second.ServiceProvider.GetRequiredService<ISagaStateStore>());
        Assert.Same(store, Assert.Single(host.Services.GetServices<ISagaStateStore>()));
        await host.StopAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingUnkeyedStoreFailsAtStartup(bool keyedOnly)
    {
        var builder = CreateBuilder();
        builder.Services.AddSagaFlow();
        if (keyedOnly) builder.Services.AddKeyedSingleton<ISagaStateStore, InMemorySagaStateStore>("keyed");
        using var host = builder.Build();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        Assert.Contains("No SagaFlow storage provider", error.Message);
        Assert.Contains("AddSingleton<ISagaStateStore>", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConflictingProvidersFailAtStartupInEitherOrder(bool inMemoryFirst)
    {
        var builder = CreateBuilder();
        builder.Services.AddSagaFlow();
        if (inMemoryFirst) builder.Services.AddSagaFlowInMemoryStorage();
        builder.Services.AddSingleton<ISagaStateStore>(_ => throw new InvalidOperationException("Conflicting factory should not be invoked."));
        if (!inMemoryFirst) builder.Services.AddSagaFlowInMemoryStorage();
        using var host = builder.Build();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        Assert.Contains("Multiple SagaFlow storage providers", error.Message);
        Assert.Contains("keep exactly one", error.Message);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(ServiceLifetime.Scoped)]
    [InlineData(ServiceLifetime.Transient)]
    public async Task NonSingletonProviderFailsAtStartup(ServiceLifetime lifetime)
    {
        var builder = CreateBuilder();
        builder.Services.AddSagaFlow();
        builder.Services.Add(ServiceDescriptor.Describe(typeof(ISagaStateStore), typeof(InMemorySagaStateStore), lifetime));
        using var host = builder.Build();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        Assert.Contains("storage provider must be a singleton", error.Message);
        Assert.Contains("AddSingleton", error.Message);
    }

    [Fact]
    public async Task NullFactoryResultFailsAtStartup()
    {
        var builder = CreateBuilder();
        builder.Services.AddSagaFlow();
        builder.Services.AddSingleton<ISagaStateStore>(_ => null!);
        using var host = builder.Build();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        Assert.Contains("returned null", error.Message);
        Assert.Contains("return a store instance", error.Message);
    }

    [Fact]
    public async Task FactoryFailureReportsHowToFixTheRegistrationAndPreservesTheCause()
    {
        var builder = CreateBuilder();
        builder.Services.AddSagaFlow();
        var cause = new InvalidOperationException("Provider factory failed.");
        builder.Services.AddSingleton<ISagaStateStore>(_ => throw cause);
        using var host = builder.Build();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        Assert.Contains("could not create the registered ISagaStateStore", error.Message);
        Assert.Contains("Check the storage provider factory", error.Message);
        Assert.Same(cause, error.InnerException);
    }

    [Fact]
    public void MissingStoreIsReportedAtBuildWhenDiValidationIsEnabled()
    {
        var builder = CreateBuilder(Environments.Development);
        builder.Services.AddSagaFlow();

        var error = Assert.Throws<AggregateException>(() => builder.Build());
        Assert.Contains("Unable to resolve service for type", error.Message);
        Assert.Contains(nameof(ISagaStateStore), error.Message);
    }

    private static HostApplicationBuilder CreateBuilder(string? environment = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = environment ?? Environments.Production
        });
        builder.Logging.ClearProviders();
        return builder;
    }
}
