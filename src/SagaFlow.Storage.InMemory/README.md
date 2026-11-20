# SagaFlow.Storage.InMemory

SagaFlow.Storage.InMemory is an in-memory storage provider for SagaFlow. It stores Saga state within the application process and does not require an external database.

## Installation

```bash
dotnet add package SagaFlow.Storage.InMemory
```

## Configuration

Select the InMemory provider in your SagaFlow Hosting configuration:

```csharp
using SagaFlow.Hosting.DependencyInjection;
using SagaFlow.Storage.InMemory.DependencyInjection;

builder.Services.AddSagaFlow(options =>
{
    options.LoadConfiguration("saga/order-process.yaml");
    options.Storage.UseInMemory();
});
```

If your application sends Saga commands, also configure a messaging transport appropriate for your scenario.

## When to use it

The InMemory provider is suitable for development, testing, and scenarios where state does not need to persist.

State is held only in the application process. It is lost when the application stops or restarts and is not shared across application processes. For persistent storage, consider `SagaFlow.Storage.PostgreSQL`.

## Requirements

- .NET 10
- SagaFlow Hosting

## License

MIT. See the [license](https://github.com/kkaradag2/SagaFlow/blob/main/LICENSE).
