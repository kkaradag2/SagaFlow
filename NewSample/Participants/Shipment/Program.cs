using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Sample.Participants.Messaging;
using Sample.Participants.Shipment.Data;
using Sample.Participants.Shipment.Messaging;
using Sample.Participants.Shipment.Services;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
var connectionString = builder.Configuration.GetConnectionString("Shipment")
    ?? throw new InvalidOperationException("ConnectionStrings:Shipment is required.");

builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton(ParticipantRabbitMqSettings.Read(builder.Configuration));
builder.Services.AddSingleton<ShipmentRepository>();
builder.Services.AddSingleton<ShipmentService>();
builder.Services.AddSingleton<IParticipantCommandHandler, ShipmentCommandHandler>();
builder.Services.AddHostedService<RabbitMqParticipantWorker>();

await builder.Build().RunAsync();
