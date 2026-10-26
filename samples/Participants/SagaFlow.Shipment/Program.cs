using SagaFlow.Shipment;
using SagaFlow.Shipment.Repositories;
using SagaFlow.Shipment.Services;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<IShipmentRepository, InMemoryShipmentRepository>();
builder.Services.AddSingleton<IShipmentService, ShipmentService>();
builder.Services.AddHostedService<Worker>();
await builder.Build().RunAsync();
