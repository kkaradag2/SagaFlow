using Microsoft.AspNetCore.Builder;
using SagaFlow.Hosting.Dashboard;

namespace SagaFlow.Hosting;

public static class SagaFlowApplicationBuilderExtensions
{
    public static IApplicationBuilder UseSagaFlowDashboard(
        this IApplicationBuilder app,
        Action<SagaFlowDashboardOptions>? configure = null)
    {
        var options = new SagaFlowDashboardOptions();
        configure?.Invoke(options);

        return app.UseMiddleware<SagaFlowDashboardMiddleware>(options);
    }
}
