namespace SagaFlow.Hosting.Dashboard;

internal static class SagaFlowDashboardPath
{
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/sagaflow";
        var normalized = path.StartsWith('/') ? path : $"/{path}";
        return normalized.TrimEnd('/');
    }

    public static string Hub(string? path) => $"{Normalize(path)}/hub";
}
