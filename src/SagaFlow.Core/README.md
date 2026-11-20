# SagaFlow.Core

SagaFlow.Core provides the foundational models and contracts for Saga processes defined with SagaFlow.

SagaFlow coordinates Saga workflows inside a domain starter service in a microservice architecture. It does not require a separate orchestration server.

## Installation

```bash
dotnet add package SagaFlow.Core
```

## What it contains

SagaFlow.Core provides the building blocks used by other SagaFlow components:

- Saga process and step definitions
- Process state and context models
- Message and result contracts
- Interfaces used by coordination and storage components

## When to use it

Reference `SagaFlow.Core` directly when building a component that needs SagaFlow's foundational models and contracts.

Core alone does not execute Saga workflows. To run them in an application, use the hosting integration and a storage provider. Add the messaging package if you need RabbitMQ messaging.

## Requirements

- .NET 10

## License

MIT. See the [license](https://github.com/kkaradag2/SagaFlow/blob/main/LICENSE).
