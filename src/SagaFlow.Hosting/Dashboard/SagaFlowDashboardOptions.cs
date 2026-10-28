namespace SagaFlow.Hosting.Dashboard;

public sealed class SagaFlowDashboardOptions
{
    public bool Enabled { get; set; } = true;

    public string Path { get; set; } = "/sagaflow";

    public string? AssetRoot { get; set; }
}
