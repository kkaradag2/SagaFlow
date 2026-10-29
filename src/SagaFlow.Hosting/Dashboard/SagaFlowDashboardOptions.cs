using System.Text.Json;
using SagaFlow.Core.State;

namespace SagaFlow.Hosting.Dashboard;

public sealed class SagaFlowDashboardOptions
{
    public bool Enabled { get; set; } = true;

    public string Path { get; set; } = "/sagaflow";

    public string? AssetRoot { get; set; }

    /// <summary>Read-only presentation of the application's local starter operation. Supply only observed data.</summary>
    public Func<SagaInstance, SagaDomainStarterDisplay?>? DescribeDomainStarter { get; set; }
}

public sealed class SagaDomainStarterDisplay
{
    public string OperationName { get; init; } = "Local operation";
    public string RollbackOperationName { get; init; } = "Local rollback";
    public string? Status { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public JsonElement? Input { get; init; }
    public JsonElement? Result { get; init; }
}
