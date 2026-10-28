using System.Text.Json;

namespace SagaFlow.Core.State;

public sealed class SagaCompensationState
{
    public string StepId { get; init; } = string.Empty;
    public string Command { get; init; } = string.Empty;
    public SagaCompensationStatus Status { get; set; } = SagaCompensationStatus.Pending;
    public int? ExecutionOrder { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public JsonElement? Result { get; set; }
}

public enum SagaCompensationStatus { Pending, Planned, Dispatched, Completed, Failed }
