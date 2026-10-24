using Grpc.Core;
using Grpc.Net.Client;
using SagaFlow.Core.Definitions;
using SagaFlow.Engine.Discovery;
using SagaFlow.Hosting.Grpc.V1;

namespace SagaFlow.Hosting.Definitions;

public sealed class GrpcRemoteDefinitionClient : IRemoteDefinitionClient
{
    public async Task<RemoteDefinitionEnvelope> GetAsync(Uri endpoint, BigPictureSourceDefinition source, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // No redirects or proxy fallback to an origin not approved by discovery policy.
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false };
        using var channel = GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions { HttpHandler = handler, MaxReceiveMessageSize = 2 * 1_048_576 });
        var client = new ProcessDefinitions.ProcessDefinitionsClient(channel);
        try
        {
            var response = await client.GetDefinitionAsync(new GetDefinitionRequest
            { Domain = source.Domain, Process = source.Process, Version = source.Version ?? "" }, deadline: DateTime.UtcNow.Add(timeout), cancellationToken: cancellationToken);
            if (response.Identity is null) throw new RemoteDefinitionException(DiscoveryFailureKind.InvalidResponse, "Missing response identity.");
            return new((int)response.ProtocolVersion, response.Identity.Domain, response.Identity.Process, response.Identity.Version, response.CanonicalYaml);
        }
        catch (RpcException ex) when (!cancellationToken.IsCancellationRequested)
        {
            var kind = ex.StatusCode switch
            {
                StatusCode.NotFound => DiscoveryFailureKind.NotFound,
                StatusCode.FailedPrecondition => DiscoveryFailureKind.AmbiguousVersion,
                StatusCode.DeadlineExceeded or StatusCode.Cancelled => DiscoveryFailureKind.Timeout,
                StatusCode.Unavailable => DiscoveryFailureKind.Unavailable,
                _ => DiscoveryFailureKind.InvalidResponse
            };
            throw new RemoteDefinitionException(kind, $"Definition request failed ({ex.StatusCode}).");
        }
        catch (RpcException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
    }
}
