# SagaFlow.Hosting

SagaFlow.Hosting provides in-process ASP.NET Core integration for embedding SagaFlow into a domain starter microservice. It does not introduce a separately deployed orchestration server.

`AddSagaFlow` loads local YAML at startup. Opt-in publication uses `options.Publication.Enabled`, an explicit `PublishedDefinitions` allowlist, and `app.MapSagaFlowDefinitionDiscovery()`. `DefinitionDiscovery` retrieves remote definitions for static visibility only.

See [YAML and gRPC documentation](../../docs/yaml-definition-discovery.md) for configuration, endpoint trust, Protobuf contracts, and the executable three-domain demonstration.
