# Thesis Mapping

- Declarative Process Definition -> `SagaFlow.Core.Definitions`, `SagaFlow.Engine.ProcessDefinition`
- Local Coordination -> `SagaFlow.Engine.Coordination`
- Messaging Interaction -> `SagaFlow.Core.Messages`, `SagaFlow.Engine.Messaging`
- Context and State Management -> `SagaFlow.Core.State`, `SagaFlow.Engine.ContextState`
- Consistency Management -> `SagaFlow.Engine.Consistency`
- Observability and Visibility -> `SagaFlow.Dashboard`, `SagaFlow.Dashboard.Client`

`SagaFlow.Hosting` provides the in-process ASP.NET Core integration layer for embedding SagaFlow into a domain starter microservice. The representative prototype consists of the `SagaFlow.Order` Domain Starter and the independent Payment, Inventory, Shipment, and Notification participant workers under `samples/`.
