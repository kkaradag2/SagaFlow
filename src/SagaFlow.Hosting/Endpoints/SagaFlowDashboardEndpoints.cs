using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace SagaFlow.Hosting.Endpoints;

public static class SagaFlowDashboardEndpoints
{
    public static IEndpointRouteBuilder MapSagaFlowDashboard(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/sagaflow", () => Results.Text("SagaFlow dashboard placeholder"));
        return endpoints;
    }
}
