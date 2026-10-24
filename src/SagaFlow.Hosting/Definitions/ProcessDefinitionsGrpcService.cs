using Grpc.Core;
using SagaFlow.Engine.ProcessDefinition;
using SagaFlow.Hosting.Grpc.V1;

namespace SagaFlow.Hosting.Definitions;

internal sealed class ProcessDefinitionsGrpcService(ProcessDefinitionRegistry registry, DefinitionPublicationSettings settings)
    : ProcessDefinitions.ProcessDefinitionsBase
{
    private IEnumerable<RegisteredDefinition> Published()
    {
        if (!settings.Enabled) throw new RpcException(new Status(StatusCode.Unavailable, "Definition publication is disabled."));
        return registry.Snapshot().Where(d => d.Origin == DefinitionOrigin.Local && settings.PublishedDefinitions.Contains(ProcessDefinitionId.From(d.Definition)));
    }

    public override Task<ListDefinitionsResponse> ListDefinitions(ListDefinitionsRequest request, ServerCallContext context)
    {
        var response = new ListDefinitionsResponse { ProtocolVersion = 1 };
        response.Definitions.AddRange(Published().Select(d => new ProcessIdentity { Domain = d.Definition.Domain, Process = d.Definition.Name, Version = d.Definition.Version }));
        return Task.FromResult(response);
    }

    public override Task<DefinitionEnvelope> GetDefinition(GetDefinitionRequest request, ServerCallContext context)
    {
        if (!ProcessDefinitionValidator.IsIdentifier(request.Domain) || !ProcessDefinitionValidator.IsIdentifier(request.Process)
            || (request.Version.Length > 0 && !ProcessDefinitionValidator.IsVersion(request.Version)))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid process identity/version."));
        var matches = Published().Where(d => d.Definition.Domain == request.Domain && d.Definition.Name == request.Process
            && (request.Version.Length == 0 || d.Definition.Version == request.Version)).ToArray();
        if (matches.Length == 0) throw new RpcException(new Status(StatusCode.NotFound, "Published local definition not found."));
        if (matches.Length > 1) throw new RpcException(new Status(StatusCode.FailedPrecondition, "Multiple published versions exist; specify version."));
        var definition = matches[0].Definition;
        return Task.FromResult(new DefinitionEnvelope
        {
            ProtocolVersion = 1,
            Identity = new ProcessIdentity { Domain = definition.Domain, Process = definition.Name, Version = definition.Version },
            CanonicalYaml = new DefinitionYamlCodec().Serialize(definition, settings.IncludeBigPictureSources)
        });
    }
}
