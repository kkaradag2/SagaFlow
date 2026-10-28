namespace SagaFlow.Engine.Coordination;

public sealed class LateSagaOutcomeException(string message) : Exception(message);
