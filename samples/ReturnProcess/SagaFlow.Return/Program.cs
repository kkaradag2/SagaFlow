using SagaFlow.Hosting.DependencyInjection;
using SagaFlow.Hosting.Endpoints;
using SagaFlow.Messaging.RabbitMQ.DependencyInjection;
using SagaFlow.Returns;


public partial class ReturnProgram
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddControllers();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();
        builder.Services.AddSingleton<ReturnRepository>();
        builder.Services.AddScoped<ReturnService>();
        builder.Services.AddSagaFlow(options =>
        {
            options.LoadFromDirectory("saga");
            options.Publication.Enabled = builder.Configuration.GetValue<bool>("SagaFlow:PublishDefinitions");
            options.Publication.PublishedDefinitions.Add(new("return", "return-process", "1.0"));
        });
        if (builder.Configuration.GetValue("RabbitMq:Enabled", true))
            builder.Services.AddSagaFlowRabbitMq(builder.Configuration.GetRequiredSection("RabbitMq"));
        var app = builder.Build();
        if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
        if (builder.Configuration.GetValue<bool>("SagaFlow:PublishDefinitions")) app.MapSagaFlowDefinitionDiscovery();
        app.MapControllers();
        app.Run();
    }
}
