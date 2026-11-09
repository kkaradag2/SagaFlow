# SagaFlow.Hosting

SagaFlow.Hosting provides in-process ASP.NET Core integration for embedding SagaFlow into a domain starter microservice. It does not introduce a separately deployed orchestration server.

`AddSagaFlow(options => options.LoadConfiguration("saga/order-process.yaml"))` loads one local YAML file at startup. Relative paths use the host content root. Hosting reads `SagaFlow:Publication` and `SagaFlow:Discovery` from the host's `IConfiguration`. Configured scalar values take precedence over callback defaults. A configured `SagaFlow:DefinitionPath` overrides the file path.

`options.UseDashboard()` opts in to the read-only Dashboard UI, API, and SignalR hub at `/sagaflow`. To reserve a different prefix, set `Path` in its callback. Set `SagaFlow:Publication:Enabled` and use `app.MapSagaFlowDefinitionDiscoveryIfEnabled()` to publish all locally owned definitions. Discovery connects to the HTTP(S) origins declared in the YAML references.

See [YAML and gRPC documentation](../../docs/03-yaml-definition-discovery.md) for configuration, endpoint trust, Protobuf contracts, and the executable three-domain demonstration.

`ISagaFlow`, `SagaCoordinator`, and the outcome entry point are scoped. Select exactly one singleton `ISagaStateStore` through `options.Storage.UseInMemory()` or `options.Storage.UsePostgreSql(...)`. Reference the selected provider project and import its `DependencyInjection` namespace. Custom providers can still register an unkeyed singleton `ISagaStateStore`. Use `options.Messaging.UseRabbitMq(...)` for a managed connection. Hosting composes these extensions without referencing the concrete providers.

`StartAsync(payload)` requires exactly one local process definition and an application-supplied `ISagaMessageBus`. See [runtime integration and lifetime details](../../docs/04-local-saga-coordination.md).
