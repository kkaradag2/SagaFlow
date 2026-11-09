using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using SagaFlow.Core.Abstractions;
using SagaFlow.Engine.Messaging;
using SagaFlow.Hosting.DependencyInjection;

namespace SagaFlow.Messaging.RabbitMQ.DependencyInjection;

public static class RabbitMqServiceCollectionExtensions
{
    /// <summary>Selects the managed RabbitMQ transport with an AMQP connection URI.</summary>
    public static void UseRabbitMq(this SagaFlowMessagingOptions messaging, Action<RabbitMqOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        messaging.Select(services =>
        {
            // The host exposes its configuration through the builder context before DI is built.
            var context = services.LastOrDefault(service =>
                service.ServiceType == typeof(HostBuilderContext) && !service.IsKeyedService)?.ImplementationInstance as HostBuilderContext;
            var configuration = context?.Configuration;
            var section = configuration?.GetSection("RabbitMq");
            if (section?.GetValue<bool?>("Enabled") == false) return;

            services.AddSagaFlowRabbitMq(options =>
            {
                section?.Bind(options);
                configure(options);
                if (string.IsNullOrWhiteSpace(options.ConnectionString))
                    throw new InvalidOperationException("Set the RabbitMQ ConnectionString when calling options.Messaging.UseRabbitMq.");
            });
        });
    }

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
        return AddTransport(services, options);
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
        return AddTransport(services, options);
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

    private static IServiceCollection AddTransport(IServiceCollection services, RabbitMqOptions options)
    {
        services.AddSingleton<RabbitMqTransport>();
        services.AddSingleton<ISagaMessageBus>(provider => provider.GetRequiredService<RabbitMqTransport>());
        services.AddHostedService(provider => provider.GetRequiredService<RabbitMqTransport>());
        if (options.EnableProcessInvocations)
        {
            services.AddScoped<ProcessInvocationPublisher>();
            services.AddScoped<ProcessInvocationReceiver>();
            services.AddSingleton<RabbitMqInvocationTransport>();
            services.AddSingleton<IProcessInvocationBus>(provider => provider.GetRequiredService<RabbitMqInvocationTransport>());
            services.AddSingleton<IProcessInvocationReconciliation>(provider => provider.GetRequiredService<RabbitMqInvocationTransport>());
            services.AddHostedService(provider => provider.GetRequiredService<RabbitMqInvocationTransport>());
        }
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
