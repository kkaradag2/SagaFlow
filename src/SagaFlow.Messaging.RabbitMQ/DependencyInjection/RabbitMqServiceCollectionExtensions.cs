using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using SagaFlow.Core.Abstractions;
using SagaFlow.Engine.Messaging;

namespace SagaFlow.Messaging.RabbitMQ.DependencyInjection;

public static class RabbitMqServiceCollectionExtensions
{
    public static IServiceCollection AddSagaFlowRabbitMq(this IServiceCollection services) =>
        services.AddSagaFlowRabbitMqUsingHostConnection(_ => { });

    public static IServiceCollection AddSagaFlowRabbitMqUsingHostConnection(
        this IServiceCollection services,
        Action<RabbitMqOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        EnsureNotRegistered(services);
        EnsureSagaFlowRegistered(services);
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(global::RabbitMQ.Client.IConnection)))
            throw new InvalidOperationException("Host-owned RabbitMQ mode requires an IConnection registration before AddSagaFlowRabbitMq is called.");

        var options = CreateOptions(configure, managedConnection: false);
        services.AddSingleton(options);
        services.AddSingleton(provider => new RabbitMqConnectionProvider(
            provider.GetRequiredService<global::RabbitMQ.Client.IConnection>()));
        return AddTransport(services);
    }

    public static IServiceCollection AddSagaFlowRabbitMq(
        this IServiceCollection services,
        Action<RabbitMqOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        EnsureNotRegistered(services);
        EnsureSagaFlowRegistered(services);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(global::RabbitMQ.Client.IConnection)))
            throw new InvalidOperationException("Managed RabbitMQ mode cannot be combined with an existing IConnection registration. Use AddSagaFlowRabbitMqUsingHostConnection instead.");

        var options = CreateOptions(configure, managedConnection: true);
        services.AddSingleton(options);
        services.AddSingleton(provider =>
        {
            if (provider.GetService<global::RabbitMQ.Client.IConnection>() is not null)
                throw new InvalidOperationException("Managed RabbitMQ mode cannot be combined with an IConnection registration.");
            return new RabbitMqConnectionProvider(options);
        });
        return AddTransport(services);
    }

    public static IServiceCollection AddSagaFlowRabbitMq(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return services.AddSagaFlowRabbitMq(options => configuration.Bind(options));
    }

    private static RabbitMqOptions CreateOptions(Action<RabbitMqOptions> configure, bool managedConnection)
    {
        var options = new RabbitMqOptions();
        configure(options);
        options.Validate(managedConnection);
        return options;
    }

    private static IServiceCollection AddTransport(IServiceCollection services)
    {
        services.AddSingleton<RabbitMqTransport>();
        services.AddSingleton<ISagaMessageBus>(provider => provider.GetRequiredService<RabbitMqTransport>());
        services.AddHostedService(provider => provider.GetRequiredService<RabbitMqTransport>());
        return services;
    }

    private static void EnsureNotRegistered(IServiceCollection services)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(RabbitMqTransport) || descriptor.ServiceType == typeof(ISagaMessageBus)))
            throw new InvalidOperationException("A SagaFlow message transport is already registered.");
    }

    private static void EnsureSagaFlowRegistered(IServiceCollection services)
    {
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(OutcomeEventReceiver)))
            throw new InvalidOperationException("Call AddSagaFlow before registering the RabbitMQ transport.");
    }
}
