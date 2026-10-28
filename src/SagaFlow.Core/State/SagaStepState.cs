using System.Text.Json;

namespace SagaFlow.Core.State;

public sealed class SagaStepState
{
    public string StepName { get; init; } = string.Empty;
    public string StepId => StepName;
    public string Command { get; init; } = string.Empty;
    public int? ExecutionOrder { get; set; }
    public JsonElement? Input { get; set; }
    public JsonElement? Result { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public TimeSpan? TimeoutLimit { get; init; }
    public DateTimeOffset? TimedOutAt { get; set; }

    public SagaStepStatus Status { get; set; } = SagaStepStatus.Pending;
}

public enum SagaStepStatus { Pending, Dispatched, Completed, Failed, TimedOut }
