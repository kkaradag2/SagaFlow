# SagaFlow.Storage.PostgreSQL

SagaFlow.Storage.PostgreSQL provides PostgreSQL-backed storage for SagaFlow coordination state. Use it when Saga state needs to persist beyond the lifetime of an application process.

## Installation

```bash
dotnet add package SagaFlow.Storage.PostgreSQL
```

## Configuration

Select the PostgreSQL provider in your SagaFlow Hosting configuration:

```csharp
using SagaFlow.Hosting.DependencyInjection;
using SagaFlow.Storage.PostgreSQL.DependencyInjection;

builder.Services.AddSagaFlow(options =>
{
    options.LoadConfiguration("saga/order-process.yaml");
    options.Storage.UsePostgreSql(storage =>
        storage.ConnectionString = builder.Configuration
            .GetConnectionString("SagaFlow"));
});
```

Set the connection string in your application configuration or a suitable secrets provider:

```json
{
  "ConnectionStrings": {
    "SagaFlow": "Host=localhost;Port=5432;Database=sagaflow;Username=sagaflow;Password=your-password"
  }
}
```

`UsePostgreSql` initializes or updates the supported SagaFlow schema and verifies its version before hosted services start. The database must be reachable and the configured user must have the necessary permissions. Schema initialization errors prevent startup.

If your application sends Saga commands, also configure a messaging transport appropriate for your scenario. Select one storage provider per host.

## Requirements

- .NET 10
- SagaFlow Hosting
- An accessible PostgreSQL database

## License

MIT. See the [license](https://github.com/kkaradag2/SagaFlow/blob/main/LICENSE).
