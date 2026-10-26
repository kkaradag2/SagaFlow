using SagaFlow.Inventory;
using SagaFlow.Inventory.Repositories;
using SagaFlow.Inventory.Services;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<IInventoryRepository, InMemoryInventoryRepository>();
builder.Services.AddSingleton<IInventoryService, InventoryService>();
builder.Services.AddHostedService<Worker>();
await builder.Build().RunAsync();
