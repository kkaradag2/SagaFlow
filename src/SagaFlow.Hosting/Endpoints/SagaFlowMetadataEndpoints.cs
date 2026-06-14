using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace SagaFlow.Hosting.Endpoints;

public static class SagaFlowMetadataEndpoints
{
    public static IEndpointRouteBuilder MapSagaFlowMetadata(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/sagaflow/metadata", () => Results.Ok(new { name = "SagaFlow.Net", status = "skeleton" }));
        return endpoints;
    }
}
