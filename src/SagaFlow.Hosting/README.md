# SagaFlow.Hosting

SagaFlow.Hosting integrates SagaFlow into an ASP.NET Core application.

Hosting registers SagaFlow services, loads the process definition, and brings your chosen storage provider and optional messaging and Dashboard configuration together with the application. Saga execution runs inside the application and does not require a separate orchestration server.

## Installation

```bash
dotnet add package SagaFlow.Hosting
dotnet add package SagaFlow.Storage.InMemory
dotnet add package SagaFlow.Messaging.RabbitMQ
```

This example uses InMemory storage and RabbitMQ. Engine and Core are installed automatically as Hosting dependencies.

## Quick start

Add a process definition to `saga/order-process.yaml`:

```yaml
name: order-process
version: "1.0"
domain: order

steps:
  - name: payment
    command: AuthorizePayment
    compensation:
      command: RefundPayment
    transitions:
      - event: success
        nextStep: completed
      - event: failure
        nextStep: compensation
```

Configure SagaFlow and start a process in `Program.cs`:

```csharp
using SagaFlow.Core.Abstractions;
using SagaFlow.Hosting.DependencyInjection;
using SagaFlow.Messaging.RabbitMQ.DependencyInjection;
using SagaFlow.Storage.InMemory.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSagaFlow(options =>
{
    options.LoadConfiguration("saga/order-process.yaml");
    options.Storage.UseInMemory();
    options.Messaging.UseRabbitMq(rabbit =>
        rabbit.ConnectionString = builder.Configuration
            .GetConnectionString("RabbitMq"));
    options.UseDashboard();
});

var app = builder.Build();

app.MapPost("/orders", async (Order order, ISagaFlow sagaFlow) =>
{
    var sagaId = await sagaFlow.StartAsync(order);
    return Results.Accepted(value: new { sagaId });
});

app.Run();

record Order(string OrderId, decimal Amount);
```

Configure `ConnectionStrings:RabbitMq` and run participant services that handle the `AuthorizePayment` and `RefundPayment` commands. InMemory storage does not preserve state across application restarts.

## Dashboard

`options.UseDashboard()` exposes the read-only Dashboard, API, and SignalR hub at `/sagaflow` by default. No separate Dashboard package reference is required.

To change the path:

```csharp
options.UseDashboard(dashboard => dashboard.Path = "/admin/sagas");
```

## Storage

Select one storage provider for your application:

- `SagaFlow.Storage.InMemory`: in-memory storage.
- `SagaFlow.Storage.PostgreSQL`: PostgreSQL-based storage.

## Requirements

- .NET 10
- A storage provider
- Messaging configuration appropriate for your scenario

## License

MIT. See the [license](https://github.com/kkaradag2/SagaFlow/blob/main/LICENSE).
