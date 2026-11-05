# SagaFlow.Engine

The process-definition foundation now parses and validates YAML, maintains immutable registry snapshots, and builds bounded Big-Picture definition graphs through an abstract remote client. See [definition documentation](../../docs/03-yaml-definition-discovery.md).

Local coordination now creates InMemory instances, dispatches forward commands through `ISagaMessageBus`, handles outcomes/Boolean transitions, and plans compensation in reverse execution order. See [local coordination](../../docs/04-local-saga-coordination.md). RabbitMQ is registered separately, and compensation commands execute through the same versioned participant protocol. Execution contexts are stored and validated atomically; there is no persistent state or restart recovery. See the [BPI-1A wire contract](../../docs/04-local-saga-coordination.md#bpi-1a-dil-bağımsız-participant-wire-sözleşmesi).

The engine is organized around these thesis feature groups:

- Declarative Process Definition
- Local Coordination
- Messaging Interaction
- Context and State Management
- Consistency Management

Observability and Visibility is represented by `SagaFlow.Dashboard` and `SagaFlow.Dashboard.Client`.
