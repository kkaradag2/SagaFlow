# SagaFlow.Messaging.RabbitMQ

SagaFlow.Messaging.RabbitMQ provides RabbitMQ-based messaging between SagaFlow processes and participant services.

The package sends SagaFlow commands through RabbitMQ and receives participant outcomes for Saga execution. It can also enable RabbitMQ transport for cross-process invocations.

## Installation

```bash
dotnet add package SagaFlow.Messaging.RabbitMQ
```

Use this package with SagaFlow Hosting and configure a storage provider in your application.

## Configuration

Select the RabbitMQ transport in `Program.cs`:

```csharp
using SagaFlow.Hosting.DependencyInjection;
using SagaFlow.Messaging.RabbitMQ.DependencyInjection;
using SagaFlow.Storage.InMemory.DependencyInjection;

builder.Services.AddSagaFlow(options =>
{
    options.LoadConfiguration("saga/order-process.yaml");
    options.Storage.UseInMemory();

    options.Messaging.UseRabbitMq(rabbit =>
    {
        rabbit.ConnectionString = builder.Configuration
            .GetConnectionString("RabbitMq");
    });
});
```

Set the connection URI in your application configuration:

```json
{
  "ConnectionStrings": {
    "RabbitMq": "amqp://guest:guest@localhost:5672/"
  }
}
```

The connection string must be an `amqp://` or `amqps://` URI. Participant services must be running to handle the commands in the process definition and return outcomes.

## Cross-process invocations

To enable RabbitMQ transport for cross-process invocations:

```csharp
options.Messaging.UseRabbitMq(rabbit =>
{
    rabbit.ConnectionString = builder.Configuration
        .GetConnectionString("RabbitMq");
    rabbit.EnableProcessInvocations = true;
});
```

## Using an existing RabbitMQ connection

If your application manages its own RabbitMQ connection, register a `RabbitMQ.Client.IConnection` and configure the SagaFlow transport with `AddSagaFlowRabbitMqUsingHostConnection`. The application supplies and manages the connection.

## Requirements

- .NET 10
- An accessible RabbitMQ server
- SagaFlow Hosting and a storage provider

## License

MIT. See the [license](https://github.com/kkaradag2/SagaFlow/blob/main/LICENSE).
