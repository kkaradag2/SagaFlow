using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using SagaFlow.Hosting.DependencyInjection;
using SagaFlow.Hosting.Endpoints;

namespace SagaFlow.Hosting.Dashboard;

internal sealed class SagaFlowDashboardStartupFilter(SagaFlowOptions options) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        var dashboard = options.Dashboard;
        var dashboardPath = new PathString(dashboard.Path);
        var dashboardEndpoint = new DashboardEndpointMarker();

        // Startup filters run outside WebApplication's route builder. A branch owns only the
        // Dashboard prefix and uses the framework's routing and authorization middleware.
        app.MapWhen(context => context.Request.Path.StartsWithSegments(dashboardPath), branch =>
        {
            branch.UseRouting();
            var services = branch.ApplicationServices.GetRequiredService<IServiceProviderIsService>();
            if (services.IsService(typeof(IAuthenticationSchemeProvider))) branch.UseAuthentication();
            if (services.IsService(typeof(IAuthorizationHandlerProvider))) branch.UseAuthorization();
            branch.UseEndpoints(endpoints =>
            {
                endpoints.MapDashboardApi(dashboard.Path, dashboard).WithMetadata(dashboardEndpoint);
                endpoints.MapHub<SagaFlowHub>(SagaFlowDashboardPath.Hub(dashboard.Path)).WithMetadata(dashboardEndpoint);
                endpoints.MapMethods($"{dashboard.Path}/{{**asset}}", [HttpMethods.Get, HttpMethods.Head],
                    context => SagaFlowDashboardMiddleware.ServeAssetsAsync(context, dashboard, dashboard.Path))
                    .WithMetadata(dashboardEndpoint)
                    .WithOrder(int.MaxValue);
            });
        });
        next(app);

        var dashboardSegments = dashboard.Path[1..].Split('/');
        var conflict = app.ApplicationServices.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .FirstOrDefault(endpoint => endpoint.Metadata.GetMetadata<DashboardEndpointMarker>() is null &&
                ReservesPath(endpoint, dashboardSegments));
        if (conflict is not null)
            throw new InvalidOperationException($"The SagaFlow Dashboard path '{dashboard.Path}' conflicts with application route '{conflict.RoutePattern.RawText}'. Choose a different Dashboard path.");
    };

    private sealed class DashboardEndpointMarker;

    private static bool ReservesPath(RouteEndpoint endpoint, string[] dashboardSegments)
    {
        var segments = endpoint.RoutePattern.PathSegments;
        return segments.Count >= dashboardSegments.Length && dashboardSegments.Select((segment, index) =>
            segments[index].Parts is [RoutePatternLiteralPart literal] &&
            string.Equals(literal.Content, segment, StringComparison.OrdinalIgnoreCase)).All(matches => matches);
    }
}
