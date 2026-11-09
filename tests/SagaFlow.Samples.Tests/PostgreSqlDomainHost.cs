using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SagaFlow.Hosting.DependencyInjection;
using SagaFlow.Hosting.Endpoints;
using SagaFlow.Messaging.RabbitMQ.DependencyInjection;
using SagaFlow.Order.DependencyInjection;
using SagaFlow.Procurement;
using SagaFlow.Storage.PostgreSQL.DependencyInjection;

namespace SagaFlow.Samples.Tests;

/// <summary>Runs the sample business services in a separate process with explicit PostgreSQL storage for integration tests.</summary>
internal static class PostgreSqlDomainHost
{
    public static async Task Main(string[] args)
    {
        (Type EntryPoint, Action<IServiceCollection> RegisterServices) domain = args[0] switch
        {
            "Order" => (typeof(global::Program), RegisterOrder),
            "Return" => (typeof(ReturnProgram), ReturnProgram.AddDomainServices),
            "Procurement" => (typeof(ProcurementProgram), ProcurementProgram.AddDomainServices),
            _ => throw new ArgumentException("Expected Order, Return or Procurement.")
        };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = domain.EntryPoint.Assembly.GetName().Name,
            Args = args[1..]
        });
        domain.RegisterServices(builder.Services);
        builder.Services.AddSagaFlow(options =>
        {
            options.LoadConfiguration($"saga/{args[0].ToLowerInvariant()}-process.yaml");
            options.Storage.UsePostgreSql(storage =>
                storage.ConnectionString = builder.Configuration.GetConnectionString("SagaFlow"));
            options.Messaging.UseRabbitMq(rabbit =>
                rabbit.ConnectionString = builder.Configuration.GetConnectionString("RabbitMq"));
            options.UseDashboard();
        });
        await using var app = builder.Build();
        app.MapSagaFlowDefinitionDiscoveryIfEnabled();
        app.MapControllers();
        await app.RunAsync();
    }

    private static void RegisterOrder(IServiceCollection services)
    {
        services.AddOrderApi();
        global::Program.AddDomainServices(services);
    }
}
