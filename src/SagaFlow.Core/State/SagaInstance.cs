using System.Text.Json;
using System.Text.Json.Serialization;

namespace SagaFlow.Core.State;

public sealed class SagaInstance
{
    public SagaFlow.Core.Messages.ProcessInvocationTerminalOutcome? InvocationTerminalOutcome { get; set; }
    public IncomingInvocation? IncomingInvocation { get; set; }
    /// <summary>The reported participant outcome preserved when the execution history was missing.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SagaFlow.Core.Messages.SagaOutcomeEvent? RecoveryOutcome { get; init; }
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string DefinitionName { get; init; } = string.Empty;

    public string DefinitionDomain { get; init; } = string.Empty;
    public string DefinitionVersion { get; init; } = string.Empty;
    public string CorrelationId { get; init; } = string.Empty;
    public string? CurrentStep { get; set; }
    public JsonElement Context { get; set; } = JsonSerializer.SerializeToElement<object?>(null);
    public string? PayloadType { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? FailureReason { get; set; }

    /// <summary>Store concurrency token. SaveAsync rejects stale revisions; use UpdateAsync for atomic mutations.</summary>
    public long Revision { get; set; }

    public SagaStatus Status { get; set; } = SagaStatus.Pending;

    public IReadOnlyList<SagaStepState> Steps { get; init; } = [];
    public IReadOnlyList<SagaCompensationState> Compensations { get; set; } = [];
    public SagaDomainRollbackState? DomainRollback { get; set; }
}
