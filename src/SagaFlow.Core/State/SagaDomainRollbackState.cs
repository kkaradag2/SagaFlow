namespace SagaFlow.Core.State;

public sealed class SagaDomainRollbackState
{
    public SagaDomainRollbackStatus Status { get; set; } = SagaDomainRollbackStatus.Planned;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
}

public enum SagaDomainRollbackStatus { Planned, Executing, Completed, Failed }
