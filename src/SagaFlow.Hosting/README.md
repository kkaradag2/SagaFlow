# SagaFlow.Hosting

SagaFlow.Hosting provides in-process ASP.NET Core integration for embedding SagaFlow into a domain starter microservice. It does not introduce a separately deployed orchestration server.

`AddSagaFlow` loads local YAML at startup and automatically hosts the read-only dashboard at `/sagaflow`. Configure it through `options.Dashboard`, or set `options.Dashboard.Enabled = false` to disable it. Opt-in publication uses `options.Publication.Enabled`, an explicit `PublishedDefinitions` allowlist, and `app.MapSagaFlowDefinitionDiscovery()`. `DefinitionDiscovery` retrieves remote definitions for static visibility only.

See [YAML and gRPC documentation](../../docs/yaml-definition-discovery.md) for configuration, endpoint trust, Protobuf contracts, and the executable three-domain demonstration.

`ISagaFlow`/`SagaCoordinator` and the outcome entry point are scoped; the default `ISagaStateStore` is a replaceable singleton InMemory adapter. `StartAsync(payload)` requires exactly one local process definition and an application-supplied `ISagaMessageBus`; no transport is silently substituted. See [runtime integration and lifetime details](../../docs/local-saga-coordination.md).
