using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using SagaFlow.Hosting.DependencyInjection;

namespace SagaFlow.Hosting.Dashboard;

internal sealed class SagaFlowDashboardStartupFilter(SagaFlowOptions options) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        if (options.Dashboard.Enabled)
        {
            app.UseRouting();
            app.UseEndpoints(endpoints =>
                endpoints.MapHub<SagaFlowHub>(SagaFlowDashboardPath.Hub(options.Dashboard.Path)));
        }
        app.UseSagaFlowDashboard(dashboard =>
        {
            dashboard.Enabled = options.Dashboard.Enabled;
            dashboard.Path = options.Dashboard.Path;
            dashboard.AssetRoot = options.Dashboard.AssetRoot;
        });
        next(app);
    };
}
