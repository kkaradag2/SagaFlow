using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Sample.Participants.Inventory.Data;
using Sample.Participants.Inventory.Messaging;
using Sample.Participants.Inventory.Services;
using Sample.Participants.Messaging;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
var connectionString = builder.Configuration.GetConnectionString("Inventory")
    ?? throw new InvalidOperationException("ConnectionStrings:Inventory is required.");

builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton(ParticipantRabbitMqSettings.Read(builder.Configuration));
builder.Services.AddSingleton<InventoryRepository>();
builder.Services.AddSingleton<InventoryService>();
builder.Services.AddSingleton<IParticipantCommandHandler, InventoryCommandHandler>();
builder.Services.AddHostedService<RabbitMqParticipantWorker>();

await builder.Build().RunAsync();
