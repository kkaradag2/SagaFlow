namespace SagaFlow.Hosting.Dashboard;

public sealed class SagaFlowDashboardOptions
{
    public string Path { get; set; } = "/sagaflow";

    public string? AssetRoot { get; set; }
}
