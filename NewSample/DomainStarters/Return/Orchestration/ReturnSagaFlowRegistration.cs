using SagaFlow.Hosting.DependencyInjection;
using SagaFlow.Messaging.RabbitMQ.DependencyInjection;
using SagaFlow.Storage.PostgreSQL.DependencyInjection;

namespace Sample.DomainStarters.Return.Orchestration;

internal static class ReturnSagaFlowRegistration
{
    public static void AddReturnSagaFlow(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSagaFlow(options =>
        {
            options.LoadConfiguration("saga/return-process.yaml");
            options.Storage.UsePostgreSql(storage =>
            {
                storage.ConnectionString = configuration.GetConnectionString("Return");
            });
            options.Messaging.UseRabbitMq(rabbit =>
            {
                rabbit.ConnectionString = configuration.GetConnectionString("RabbitMq");
                rabbit.ClientProvidedName = "newsample-return";
            });
            options.UseDashboard();
        });
    }
}
