using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SagaFlow.Hosting.Definitions;

namespace SagaFlow.Hosting.Endpoints;

public static class SagaFlowDefinitionEndpoints
{
    public static IEndpointConventionBuilder MapSagaFlowDefinitionDiscovery(this IEndpointRouteBuilder endpoints)
    {
        var settings = endpoints.ServiceProvider.GetService<DefinitionPublicationSettings>();
        if (settings is null || !settings.Enabled)
            throw new InvalidOperationException("Enable AddSagaFlow options.Publication.Enabled and explicitly select published local definitions before mapping gRPC discovery.");
        var mapping = endpoints.ServiceProvider.GetRequiredService<DefinitionEndpointMapping>();
        if (mapping.IsMapped) throw new InvalidOperationException("SagaFlow definition discovery is already mapped.");
        var endpoint = endpoints.MapGrpcService<ProcessDefinitionsGrpcService>();
        mapping.IsMapped = true;
        return endpoint;
    }
}
