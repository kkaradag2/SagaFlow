# SagaFlow.Net

SagaFlow.Net is a lightweight .NET library and embedded dashboard for defining, executing, and observing local-orchestration-based Saga workflows in microservice architectures.

SagaFlow.Net is developed as the software artifact of the thesis titled "A Local-Orchestration-Based Reference Architecture for Hybrid Saga Coordination in Microservice Architectures."

Current status: SagaFlow loads YAML definitions, optionally discovers definitions through gRPC, and coordinates Saga execution and compensation inside the Domain Starter. Applications select InMemory or PostgreSQL for coordination state and can enable RabbitMQ for messaging. See [local coordination and state semantics](docs/04-local-saga-coordination.md) and [definition/discovery documentation](docs/03-yaml-definition-discovery.md).

SagaFlow does not require a separately deployed orchestration server. The engine is embedded into the domain starter microservice through the hosting integration.

## Project Structure

```text
src/
  SagaFlow.Core/
  SagaFlow.Engine/
  SagaFlow.Hosting/
  SagaFlow.Dashboard/
  SagaFlow.Dashboard.Client/
samples/
  OrderProcess/SagaFlow.Order/
  ProcurementProcess/SagaFlow.Procurement/
  Participants/SagaFlow.Payment/
  Participants/SagaFlow.Inventory/
  Participants/SagaFlow.Shipment/
  Participants/SagaFlow.Notification/
tests/
  SagaFlow.Core.Tests/
  SagaFlow.Engine.Tests/
  SagaFlow.Hosting.Tests/
  SagaFlow.Samples.Tests/
docs/
  02-thesis-mapping.md
```

## Project Mapping

- `SagaFlow.Core`: conceptual model, definitions, contracts.
- `SagaFlow.Engine`: local orchestration, state, transitions, compensation.
- `SagaFlow.Hosting`: in-process ASP.NET Core integration.
- `SagaFlow.Dashboard`: embedded dashboard asset host.
- `SagaFlow.Dashboard.Client`: React/Vite dashboard source.
- `SagaFlow.Order`: OrderProcess Domain Starter and `POST /orders` API.
- `SagaFlow.Procurement`: purchase-request Domain Starter; reuses ManagerApproval, then invokes Order through RabbitMQ. [Run instructions and boundaries](docs/06-manager-approval-sample.md#s2-7a-procurement--order-poc).
- `SagaFlow.Payment`, `SagaFlow.Inventory`, `SagaFlow.Shipment`, `SagaFlow.Notification`: independent participant Worker Services using business-only JSON and AMQP execution metadata, without a SagaFlow runtime/SDK dependency. [BPI-1A wire contract](docs/04-local-saga-coordination.md#bpi-1a-dil-bağımsız-participant-wire-sözleşmesi).

## Feature Model Alignment

The internal structure of SagaFlow intentionally follows the six feature groups of the thesis feature model. The first five groups are represented mainly in Core and Engine, while Observability and Visibility is represented by the embedded Dashboard and Dashboard.Client projects.

- Declarative Process Definition: `SagaFlow.Core.Definitions`, `SagaFlow.Engine.ProcessDefinition`
- Local Coordination: `SagaFlow.Engine.Coordination`
- Messaging Interaction: `SagaFlow.Core.Messages`, `SagaFlow.Engine.Messaging`
- Context and State Management: `SagaFlow.Core.State`, `SagaFlow.Engine.ContextState`
- Consistency Management: `SagaFlow.Engine.Consistency`
- Observability and Visibility: `SagaFlow.Dashboard`, `SagaFlow.Dashboard.Client`

## Intended Usage

```csharp
using SagaFlow.Hosting.DependencyInjection;
using SagaFlow.Messaging.RabbitMQ.DependencyInjection;
using SagaFlow.Storage.PostgreSQL.DependencyInjection;

builder.Services.AddSagaFlow(options =>
{
    options.LoadConfiguration("saga/order-process.yaml");
    options.Storage.UsePostgreSql(storage =>
        storage.ConnectionString = builder.Configuration.GetConnectionString("SagaFlow"));
    options.Messaging.UseRabbitMq(rabbit =>
        rabbit.ConnectionString = builder.Configuration.GetConnectionString("RabbitMq"));
    options.UseDashboard();
});
```

Select exactly one storage provider. Reference `SagaFlow.Storage.InMemory` and import
`SagaFlow.Storage.InMemory.DependencyInjection` to call `options.Storage.UseInMemory()`.
PostgreSQL initializes and validates its schema before hosted services start.
RabbitMQ accepts an `amqp://` or `amqps://` connection URI. A host can omit messaging
when it only loads or publishes definitions. The Order, Return, and Procurement samples select InMemory in code and show the PostgreSQL call immediately below it as an alternative.

`options.UseDashboard()` automatically exposes the read-only UI, API, and SignalR hub
at `/sagaflow`. Set a custom prefix with `options.UseDashboard(dashboard => dashboard.Path = "/admin/sagas")`.
Omit `UseDashboard()` to keep the Dashboard disabled.

## Build

```powershell
dotnet build
dotnet test
```

The dashboard client can be built separately:

```powershell
cd src/SagaFlow.Dashboard.Client
npm install
npm run build
```

The dashboard client build output is copied to `src/SagaFlow.Dashboard/wwwroot/dashboard` and served by the sample at `/sagaflow`.
