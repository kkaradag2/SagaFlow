using SagaFlow.Core.Abstractions;
using SagaFlow.Hosting.DependencyInjection;
using SagaFlow.Messaging.RabbitMQ.DependencyInjection;
using SagaFlow.Storage.PostgreSQL.DependencyInjection;
using Sample.DomainStarters.Order.Services;

namespace Sample.DomainStarters.Order.Orchestration;

internal static class OrderSagaFlowRegistration
{
    public static void AddOrderSagaFlow(this IServiceCollection services, IConfiguration configuration)
    {
        // SagaFlow resolves the closed handler using the payload type saved with each Order Saga.
        services.AddScoped(typeof(SagaRollbackHandler<>), typeof(OrderRollbackHandler<>));
        services.AddSagaFlow(options =>
        {
            options.LoadConfiguration("saga/order-process.yaml");
            options.Storage.UsePostgreSql(storage =>
            {
                storage.ConnectionString = configuration.GetConnectionString("Order");
            });
            options.Messaging.UseRabbitMq(rabbit =>
            {
                rabbit.ConnectionString = configuration.GetConnectionString("RabbitMq");
                rabbit.ClientProvidedName = "newsample-order";
            });
            options.UseDashboard();
        });
    }
}
