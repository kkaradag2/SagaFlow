namespace SagaFlow.Core.State;

public enum SagaStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Compensating,
    Compensated
}
