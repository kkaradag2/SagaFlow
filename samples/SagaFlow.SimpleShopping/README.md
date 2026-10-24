# SagaFlow.SimpleShopping

Minimal ASP.NET Core sample that loads and validates purchase YAML at startup. It exposes `/health`, canonical YAML at `/demo/purchase-definition`, and a static definition graph at `/demo/definition-graph`.

Run `dotnet run --project samples/SagaFlow.SimpleShopping`. Definition publication is disabled by default; see [configuration and the A/B/C integration demonstration](../../docs/yaml-definition-discovery.md). This sample does not execute a Saga and is not the thesis's five-service prototype.
