namespace SagaFlow.Engine.Coordination;

public sealed class SagaDispatchException(string instanceId, string stepId, Exception innerException)
    : InvalidOperationException($"Saga '{instanceId}', step '{stepId}': command publication failed: {innerException.Message}", innerException)
{
    public string InstanceId { get; } = instanceId;
    public string StepId { get; } = stepId;
}
