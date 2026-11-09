# SagaFlow.Hosting

SagaFlow.Hosting provides in-process ASP.NET Core integration for embedding SagaFlow into a domain starter microservice. It does not introduce a separately deployed orchestration server.

`AddSagaFlow` loads local YAML at startup and automatically hosts the read-only dashboard at `/sagaflow`. Configure it through `options.Dashboard`, or set `options.Dashboard.Enabled = false` to disable it. Opt-in publication uses `options.Publication.Enabled`, an explicit `PublishedDefinitions` allowlist, and `app.MapSagaFlowDefinitionDiscovery()`. `DefinitionDiscovery` retrieves remote definitions for static visibility only.

See [YAML and gRPC documentation](../../docs/03-yaml-definition-discovery.md) for configuration, endpoint trust, Protobuf contracts, and the executable three-domain demonstration.

`ISagaFlow`, `SagaCoordinator`, and the outcome entry point are scoped. The application must register exactly one unkeyed singleton `ISagaStateStore`. Reference `SagaFlow.Storage.InMemory` and call `AddSagaFlowInMemoryStorage()` from `SagaFlow.Storage.InMemory.DependencyInjection`, or register a custom store with `AddSingleton<ISagaStateStore, TStore>()`. Either registration can come before or after `AddSagaFlow()`.

`StartAsync(payload)` requires exactly one local process definition and an application-supplied `ISagaMessageBus`. See [runtime integration and lifetime details](../../docs/04-local-saga-coordination.md).
