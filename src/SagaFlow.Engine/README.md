# SagaFlow.Engine

The process-definition foundation now parses and validates YAML, maintains immutable registry snapshots, and builds bounded Big-Picture definition graphs through an abstract remote client. See [definition documentation](../../docs/yaml-definition-discovery.md).

Local coordination now creates InMemory instances, dispatches forward commands through `ISagaMessageBus`, handles outcomes/Boolean transitions, and plans compensation in reverse execution order. See [local coordination](../../docs/local-saga-coordination.md). There is no default transport, persistent state, or compensation execution.

The engine is organized around these thesis feature groups:

- Declarative Process Definition
- Local Coordination
- Messaging Interaction
- Context and State Management
- Consistency Management

Observability and Visibility is represented by `SagaFlow.Dashboard` and `SagaFlow.Dashboard.Client`.
