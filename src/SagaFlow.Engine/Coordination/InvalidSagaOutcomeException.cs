namespace SagaFlow.Engine.Coordination;

public sealed class InvalidSagaOutcomeException : InvalidOperationException
{
    public InvalidSagaOutcomeException(string message) : base(message) { }

    public InvalidSagaOutcomeException(string message, Exception innerException) : base(message, innerException) { }
}
