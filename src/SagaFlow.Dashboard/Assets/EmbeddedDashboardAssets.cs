namespace SagaFlow.Dashboard.Assets;

public sealed class EmbeddedDashboardAssets
{
    public const string RelativeDashboardPath = "wwwroot/dashboard";

    public static string GetDashboardAssetPath()
    {
        return Path.Combine(AppContext.BaseDirectory, "wwwroot", "dashboard");
    }
}
