using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SagaFlow.Hosting.DependencyInjection;
using SagaFlow.Messaging.RabbitMQ.DependencyInjection;

namespace SagaFlow.Messaging.RabbitMQ.Tests;

public sealed class RabbitMqRegistrationTests
{
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
