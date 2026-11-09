using SagaFlow.Engine.Coordination;
using SagaFlow.Hosting.DependencyInjection;
using SagaFlow.Storage.InMemory.DependencyInjection;
using SagaFlow.Hosting.Endpoints;
using SagaFlow.Messaging.RabbitMQ.DependencyInjection;

namespace SagaFlow.Procurement;

public partial class ProcurementProgram
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddControllers(); builder.Services.AddEndpointsApiExplorer(); builder.Services.AddSwaggerGen();
        builder.Services.AddSingleton(TimeProvider.System); builder.Services.AddSingleton<PurchaseRepository>();
        builder.Services.AddScoped<ProcurementService>(); builder.Services.AddScoped<ILocalSagaStepHandler, ProcurementOrderStep>();
        builder.Services.AddSagaFlow(options =>
        {
            options.LoadFromDirectory(builder.Configuration["SagaFlow:DefinitionDirectory"] ?? "saga");
            options.Publication.Enabled = builder.Configuration.GetValue<bool>("SagaFlow:PublishDefinitions");
            options.Publication.IncludeBigPictureSources = builder.Configuration.GetValue<bool>("SagaFlow:PublishBigPictureSources");
            options.Publication.PublishedDefinitions.Add(new("procurement", "procurement-process", "1.0"));
            foreach (var endpoint in builder.Configuration.GetSection("SagaFlow:Discovery:AllowedEndpoints").Get<string[]>() ?? [])
                options.Discovery.AllowedEndpoints.Add(endpoint);
            options.Discovery.AllowInsecureHttp = builder.Configuration.GetValue<bool>("SagaFlow:Discovery:AllowInsecureHttp");
            options.Dashboard.DescribeDomainStarter = instance => new() { OperationName = "CreatePurchaseRequest", RollbackOperationName = "Local failure review",
                Result = instance.Context.Clone() };
        });
        builder.Services.AddSagaFlowInMemoryStorage();
        if (builder.Configuration.GetValue("RabbitMq:Enabled", true)) builder.Services.AddSagaFlowRabbitMq(builder.Configuration.GetRequiredSection("RabbitMq"));
        var app = builder.Build();
        if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
        if (builder.Configuration.GetValue<bool>("SagaFlow:PublishDefinitions")) app.MapSagaFlowDefinitionDiscovery();
        app.MapControllers(); app.Run();
    }
}
