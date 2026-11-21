using SagaFlow.Engine.Coordination;
using SagaFlow.Hosting.DependencyInjection;
using SagaFlow.Messaging.RabbitMQ.DependencyInjection;
using SagaFlow.Storage.PostgreSQL.DependencyInjection;

namespace Sample.DomainStarters.Procurement.Orchestration;

internal static class ProcurementSagaFlowRegistration
{
    public static void AddProcurementSagaFlow(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<ILocalSagaStepHandler, ProcurementOrderStep>();
        services.AddSagaFlow(options =>
        {
            options.LoadConfiguration("saga/procurement-process.yaml");
            options.Storage.UsePostgreSql(storage => storage.ConnectionString = configuration.GetConnectionString("Procurement"));
            options.Messaging.UseRabbitMq(rabbit =>
            {
                rabbit.ConnectionString = configuration.GetConnectionString("RabbitMq");
                rabbit.ClientProvidedName = "newsample-procurement";
                rabbit.EnableProcessInvocations = true;
            });
            options.UseDashboard();
        });
    }
}
