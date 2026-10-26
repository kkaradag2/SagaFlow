namespace SagaFlow.Engine.Coordination;

public sealed class DuplicateSagaOutcomeException(string message) : InvalidOperationException(message);
