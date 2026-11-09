using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SagaFlow.Hosting.DependencyInjection;
using SagaFlow.Storage.InMemory.DependencyInjection;
using SagaFlow.Messaging.RabbitMQ.DependencyInjection;
using SagaFlow.Core.Abstractions;

namespace SagaFlow.Messaging.RabbitMQ.Tests;

public sealed class RabbitMqRegistrationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnifiedRegistrationUsesExistingHostTransportSettings(bool enabled)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RabbitMq:Enabled"] = enabled.ToString(),
            ["RabbitMq:ClientProvidedName"] = "sample-domain",
            ["RabbitMq:CommandExchange"] = "sample.commands",
            ["RabbitMq:EnableProcessInvocations"] = "true",
            ["RabbitMq:PrefetchCount"] = "5"
        });
        builder.Services.AddSagaFlow(options =>
        {
            options.Storage.UseInMemory();
            options.Messaging.UseRabbitMq(rabbit => rabbit.ConnectionString = "amqp://guest:guest@localhost/");
        });
        await using var provider = builder.Services.BuildServiceProvider();
        if (enabled)
        {
            Assert.IsType<RabbitMqInvocationTransport>(provider.GetRequiredService<IProcessInvocationBus>());
            var options = provider.GetRequiredService<RabbitMqOptions>();
            Assert.Equal("sample-domain", options.CreateConnectionFactory().ClientProvidedName);
            Assert.Equal("sample.commands", options.CommandExchange);
            Assert.Equal((ushort)5, options.PrefetchCount);
        }
        else
        {
            Assert.Null(provider.GetService<ISagaMessageBus>());
            Assert.Null(provider.GetService<IProcessInvocationBus>());
        }
    }

    [Theory]
    [InlineData("amqp://guest:guest@localhost:5672/", "guest", "guest", "/", 5672, false)]
    [InlineData("amqps://saga%40user:p%3A%2B%2F@localhost/%2Fsagas", "saga@user", "p:+/", "/sagas", 5671, true)]
    public async Task UnifiedRabbitMqRegistrationUsesTheClientUriParser(string uri, string user, string password,
        string vhost, int port, bool tls)
    {
        var services = new ServiceCollection();
        services.AddSagaFlow(options =>
        {
            options.Storage.UseInMemory();
            options.Messaging.UseRabbitMq(rabbit =>
            {
                rabbit.ConnectionString = uri;
                rabbit.EnableProcessInvocations = true;
                rabbit.CommandExchange = "custom.commands";
                rabbit.PrefetchCount = 7;
            });
        });
        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<RabbitMqOptions>();
        var factory = options.CreateConnectionFactory();
        Assert.Equal("localhost", factory.HostName);
        Assert.Equal(user, factory.UserName);
        Assert.Equal(password, factory.Password);
        Assert.Equal(vhost, factory.VirtualHost);
        Assert.Equal(port, factory.Port);
        Assert.Equal(tls, factory.Ssl.Enabled);
        Assert.Equal(System.Net.Security.SslPolicyErrors.None, factory.Ssl.AcceptablePolicyErrors);
        Assert.Equal("custom.commands", options.CommandExchange);
        Assert.Equal((ushort)7, options.PrefetchCount);
        Assert.Same(provider.GetRequiredService<RabbitMqTransport>(), provider.GetRequiredService<ISagaMessageBus>());
        Assert.IsType<RabbitMqInvocationTransport>(provider.GetRequiredService<IProcessInvocationBus>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("localhost:5672")]
    [InlineData("http://guest:secret@localhost/")]
    [InlineData("amqp://guest:secret@localhost/a/b")]
    [InlineData("amqp://guest:secret@localhost/?heartbeat=60")]
    public void InvalidUnifiedUriFailsWithoutIncludingCredentials(string? uri)
    {
        var services = new ServiceCollection();
        var error = Assert.Throws<InvalidOperationException>(() => services.AddSagaFlow(options =>
            options.Messaging.UseRabbitMq(rabbit => rabbit.ConnectionString = uri)));
        Assert.Contains("RabbitMQ ConnectionString", error.Message);
        Assert.DoesNotContain("secret", error.ToString());
    }

    [Fact]
    public void OmittingMessagingRegistersNoRabbitMqComponents()
    {
        var services = new ServiceCollection();
        services.AddSagaFlow(options => options.Storage.UseInMemory());
        using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService<ISagaMessageBus>());
        Assert.Null(provider.GetService<IProcessInvocationBus>());
        Assert.Null(provider.GetService<RabbitMqOptions>());
    }

    [Fact]
    public void ManagedRegistrationBindsStandardConfigurationSection()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RabbitMq:HostName"] = "rabbit.internal",
            ["RabbitMq:Port"] = "5678",
            ["RabbitMq:UserName"] = "saga-user",
            ["RabbitMq:Password"] = "saga-pass",
            ["RabbitMq:VirtualHost"] = "/sagas",
            ["RabbitMq:ClientProvidedName"] = "order-host",
            ["RabbitMq:CommandExchange"] = "commands",
            ["RabbitMq:PrefetchCount"] = "7"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSagaFlowInMemoryStorage();
        services.AddSagaFlow();

        services.AddSagaFlowRabbitMq(configuration.GetRequiredSection("RabbitMq"));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<RabbitMqOptions>();
        Assert.Equal("rabbit.internal", options.HostName);
        Assert.Equal(5678, options.Port);
        Assert.Equal("saga-user", options.UserName);
        Assert.Equal("saga-pass", options.Password);
        Assert.Equal("/sagas", options.VirtualHost);
        Assert.Equal("order-host", options.ClientProvidedName);
        Assert.Equal("commands", options.CommandExchange);
        Assert.Equal((ushort)7, options.PrefetchCount);
    }
}
