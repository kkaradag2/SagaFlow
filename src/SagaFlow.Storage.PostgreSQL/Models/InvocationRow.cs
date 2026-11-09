namespace SagaFlow.Storage.PostgreSQL.Models;

/// <summary>Serializes admission and target ownership for one incoming invocation UUID.</summary>
internal sealed class InvocationRow
{
    public Guid Id { get; init; }
    public string? TargetId { get; set; }
    public string? Decision { get; set; }
}
