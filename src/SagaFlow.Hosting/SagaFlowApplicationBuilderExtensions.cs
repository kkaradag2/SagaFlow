using Microsoft.AspNetCore.Builder;
using SagaFlow.Hosting.Dashboard;

namespace SagaFlow.Hosting;

public static class SagaFlowApplicationBuilderExtensions
{
    private const string DashboardRegisteredKey = "SagaFlow.Dashboard.Registered";

    public static IApplicationBuilder UseSagaFlowDashboard(
        this IApplicationBuilder app,
        Action<SagaFlowDashboardOptions>? configure = null)
    {
        var options = new SagaFlowDashboardOptions();
        configure?.Invoke(options);

        if (!options.Enabled || app.Properties.ContainsKey(DashboardRegisteredKey)) return app;
        app.Properties[DashboardRegisteredKey] = true;

        return app.UseMiddleware<SagaFlowDashboardMiddleware>(options);
    }
}
