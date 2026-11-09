using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SagaFlow.Hosting.Definitions;

namespace SagaFlow.Hosting.Endpoints;

public static class SagaFlowDefinitionEndpoints
{
    /// <summary>Maps the gRPC definition endpoint only when the host explicitly enables publication.</summary>
    public static IEndpointConventionBuilder? MapSagaFlowDefinitionDiscoveryIfEnabled(this IEndpointRouteBuilder endpoints) =>
        endpoints.ServiceProvider.GetService<DefinitionPublicationSettings>() is { Enabled: true }
            ? endpoints.MapSagaFlowDefinitionDiscovery() : null;

    public static IEndpointConventionBuilder MapSagaFlowDefinitionDiscovery(this IEndpointRouteBuilder endpoints)
    {
        var settings = endpoints.ServiceProvider.GetService<DefinitionPublicationSettings>();
        if (settings is null || !settings.Enabled)
            throw new InvalidOperationException("Enable SagaFlow:Publication:Enabled or options.Publication.Enabled before mapping gRPC discovery.");
        var mapping = endpoints.ServiceProvider.GetRequiredService<DefinitionEndpointMapping>();
        if (mapping.IsMapped) throw new InvalidOperationException("SagaFlow definition discovery is already mapped.");
        var endpoint = endpoints.MapGrpcService<ProcessDefinitionsGrpcService>();
        mapping.IsMapped = true;
        return endpoint;
    }
}
