using System.Text.Json.Serialization;
using SagaFlow.Participants.Messaging;
using SagaFlow.Warehouse;


public partial class WarehouseProgram
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddControllers().AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<WarehouseRepository>();
        builder.Services.AddSingleton<IParticipantCommandHandler, WarehouseCommandHandler>();
        builder.Services.AddSingleton<IWarehouseOutcomePublisher, WarehouseOutcomePublisher>();
        builder.Services.AddScoped<WarehouseDecisionService>();
        builder.Services.AddHostedService<RabbitMqParticipantWorker>();
        var app = builder.Build();
        if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
        app.MapControllers();
        app.Run();
    }
}
