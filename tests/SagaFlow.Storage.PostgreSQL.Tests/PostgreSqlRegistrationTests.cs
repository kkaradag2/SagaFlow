using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SagaFlow.Core.Abstractions;
using SagaFlow.Storage.PostgreSQL.DependencyInjection;

namespace SagaFlow.Storage.PostgreSQL.Tests;

public sealed class PostgreSqlRegistrationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("not a connection string")]
    [InlineData("Host=localhost")]
    [InlineData("Database=saga")]
    public void InvalidConnectionSettingsFailAtRegistration(string settings)
    {
        var services = new ServiceCollection();
        var error = Assert.ThrowsAny<ArgumentException>(() => services.AddSagaFlowPostgreSqlStorage(settings));
        Assert.Equal("connectionString", error.ParamName);
        Assert.Empty(services);
    }

    [Fact]
    public void RegistrationSelectsOneSingletonWithoutOpeningTheDatabase()
    {
        var services = new ServiceCollection();
        services.AddSagaFlowPostgreSqlStorage("Host=localhost;Database=saga");
        var descriptor = Assert.Single(services);
        Assert.Equal(typeof(ISagaStateStore), descriptor.ServiceType);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var instance = Assert.IsType<PostgreSqlSagaStateStore>(provider.GetRequiredService<ISagaStateStore>());
        Assert.Same(instance, scope.ServiceProvider.GetRequiredService<ISagaStateStore>());
    }

    [Fact]
    public async Task UnreachableServerSurfacesAConnectionError()
    {
        using var endpoint = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        endpoint.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)endpoint.LocalEndPoint!).Port;
        var store = new PostgreSqlSagaStateStore($"Host=127.0.0.1;Port={port};Database=saga;Username=test;Timeout=1;Pooling=false");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.FindAsync("missing"));
        Assert.Contains("Failed to connect", Assert.IsType<NpgsqlException>(error.InnerException).Message);
    }
}
