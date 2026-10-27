using SagaFlow.Inventory;
using SagaFlow.Inventory.Repositories;
using SagaFlow.Inventory.Services;
using SagaFlow.Inventory.Messaging;
using SagaFlow.Participants.Messaging;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<IInventoryRepository, InMemoryInventoryRepository>();
builder.Services.AddSingleton<IInventoryService, InventoryService>();
builder.Services.AddSingleton<IParticipantCommandHandler, InventoryCommandHandler>();
builder.Services.AddHostedService<Worker>();
await builder.Build().RunAsync();
