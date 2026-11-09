using System.Text.Json;
using FluentValidation;
using SagaFlow.Hosting.DependencyInjection;
using SagaFlow.Hosting.Endpoints;
using SagaFlow.Messaging.RabbitMQ.DependencyInjection;
using SagaFlow.Storage.InMemory.DependencyInjection;
using SagaFlow.Storage.PostgreSQL.DependencyInjection;
using SagaFlow.Order.DependencyInjection;
using SagaFlow.Order.Domain;
using SagaFlow.Order.Repositories;
using SagaFlow.Order.Services;
using SagaFlow.Order.Validators;
using SagaFlow.Engine.Messaging;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOrderApi();
Program.AddDomainServices(builder.Services);
builder.Services.AddSagaFlow(options =>
{
    options.LoadConfiguration("saga/order-process.yaml");

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

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapSagaFlowDefinitionDiscoveryIfEnabled();
app.MapControllers();

await app.RunAsync();

public partial class Program
{
    internal static void AddDomainServices(IServiceCollection services)
    {
        services.AddSingleton<IOrderRepository, InMemoryOrderRepository>();
        services.AddSingleton<IValidator<CreateOrderRequest>, CreateOrderRequestValidator>();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IOrderService, OrderService>();
        services.AddSagaRollbackHandler<Order, OrderRollbackHandler>();
        services.AddSagaRollbackHandler<JsonElement, IncomingOrderRollbackHandler>();
        services.AddSingleton<IncomingOrderService>();
        services.AddSingleton<IProcessInvocationPolicy>(services => services.GetRequiredService<IncomingOrderService>());
        services.AddSingleton<IIncomingInvocationInitializer>(services => services.GetRequiredService<IncomingOrderService>());
    }
}
