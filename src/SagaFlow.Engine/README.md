# SagaFlow.Engine

SagaFlow.Engine is the execution engine that processes SagaFlow process definitions and coordinates Saga execution. It runs inside the domain starter service and does not require a separate orchestration server.

## Responsibilities

- Load and validate YAML process definitions.
- Coordinate Saga steps and transitions.
- Evaluate step outcomes and conditional transitions.
- Plan and execute compensation when needed.
- Manage process state, execution context, and timeouts.
- Provide infrastructure for process definition discovery.

## When to use it directly

Reference Engine directly when building a custom integration that works with SagaFlow's execution or coordination components:

```bash
dotnet add package SagaFlow.Engine
```

For an ASP.NET Core application, use [SagaFlow.Hosting](https://github.com/kkaradag2/SagaFlow/tree/main/src/SagaFlow.Hosting). Hosting provides the application integration and brings in Engine as a dependency.

## Requirements

- .NET 10

## License

MIT. See the [license](https://github.com/kkaradag2/SagaFlow/blob/main/LICENSE).
