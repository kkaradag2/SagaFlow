# SagaFlow.Net

SagaFlow.Net is a lightweight .NET library and embedded dashboard for defining, executing, and observing local-orchestration-based Saga workflows in microservice architectures.

SagaFlow.Net is developed as the software artifact of the thesis titled "A Local-Orchestration-Based Reference Architecture for Hybrid Saga Coordination in Microservice Architectures."

Current status: YAML process definitions, optional gRPC Big-Picture discovery, and the first local Saga coordination PoC are implemented. `ISagaFlow.StartAsync(payload)` creates volatile InMemory state and dispatches YAML commands through the application-provided `ISagaMessageBus` port. No default message transport, durable state, or compensation execution is provided. See [local coordination and state semantics](docs/local-saga-coordination.md) and [definition/discovery documentation](docs/yaml-definition-discovery.md).

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
  thesis-mapping.md
```

## Project Mapping

- `SagaFlow.Core`: conceptual model, definitions, contracts.
- `SagaFlow.Engine`: local orchestration, state, transitions, compensation.
- `SagaFlow.Hosting`: in-process ASP.NET Core integration.
- `SagaFlow.Dashboard`: embedded dashboard asset host.
- `SagaFlow.Dashboard.Client`: React/Vite dashboard source.
- `SagaFlow.Order`: OrderProcess Domain Starter and `POST /orders` API.
- `SagaFlow.Payment`, `SagaFlow.Inventory`, `SagaFlow.Shipment`, `SagaFlow.Notification`: independent participant Worker Services prepared for the later messaging phase.

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
builder.Services.AddSagaFlow(options =>
{
    options.LoadFromYaml("saga/order-process.yaml");
    // Dashboard is enabled by default at /sagaflow.
    // options.Dashboard.Path = "/dashboard";
});
```

`AddSagaFlow()` automatically hosts the read-only dashboard and its state API. Set
`options.Dashboard.Enabled = false` to disable it. `UseSagaFlowDashboard()` remains
available for applications that need to register the middleware explicitly.

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
