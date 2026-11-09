using SagaFlow.Engine.Coordination;
using SagaFlow.Hosting.DependencyInjection;
using SagaFlow.Hosting.Endpoints;
using SagaFlow.Messaging.RabbitMQ.DependencyInjection;
using SagaFlow.Storage.InMemory.DependencyInjection;
using SagaFlow.Storage.PostgreSQL.DependencyInjection;

namespace SagaFlow.Procurement;

public partial class ProcurementProgram
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        AddDomainServices(builder.Services);
        builder.Services.AddSagaFlow(options =>
        {
            options.LoadConfiguration("saga/procurement-process.yaml");

            options.Storage.UseInMemory();

            // Alternative: PostgreSQL
            // options.Storage.UsePostgreSql(storage =>
            // {
            //     storage.ConnectionString = builder.Configuration
            //         .GetConnectionString("SagaFlow");
            // });

            options.Messaging.UseRabbitMq(rabbit =>
            {
                rabbit.ConnectionString = builder.Configuration
                    .GetConnectionString("RabbitMq");
            });

            options.UseDashboard();
        });

        var app = builder.Build();
        if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
        app.MapSagaFlowDefinitionDiscoveryIfEnabled();
        app.MapControllers(); await app.RunAsync();
    }

    internal static void AddDomainServices(IServiceCollection services)
    {
        services.AddControllers(); services.AddEndpointsApiExplorer(); services.AddSwaggerGen();
        services.AddSingleton(TimeProvider.System); services.AddSingleton<PurchaseRepository>();
        services.AddScoped<ProcurementService>(); services.AddScoped<ILocalSagaStepHandler, ProcurementOrderStep>();
    }
}
