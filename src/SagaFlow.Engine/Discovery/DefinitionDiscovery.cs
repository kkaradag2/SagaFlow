using SagaFlow.Core.Definitions;
using SagaFlow.Engine.ProcessDefinition;
using System.Diagnostics;

namespace SagaFlow.Engine.Discovery;

public sealed record RemoteDefinitionEnvelope(int ProtocolVersion, string Domain, string Process, string Version, string CanonicalYaml);

public interface IRemoteDefinitionClient
{
    Task<RemoteDefinitionEnvelope> GetAsync(Uri endpoint, BigPictureSourceDefinition source, TimeSpan timeout, CancellationToken cancellationToken);
}

public enum DiscoveryFailureKind { UntrustedEndpoint, NotFound, AmbiguousVersion, Unavailable, Timeout, InvalidResponse, Cycle, LimitExceeded, OverallTimeout }
public sealed class RemoteDefinitionException(DiscoveryFailureKind kind, string message) : Exception(message)
{
    public DiscoveryFailureKind Kind { get; } = kind;
}

public sealed class DefinitionDiscoveryOptions
{
    /// <summary>Exact HTTP(S) origins; deny by default. HTTPS is required unless explicitly enabled for a local PoC.</summary>
    public ISet<string> AllowedEndpoints { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public bool AllowInsecureHttp { get; set; }
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(3);
    public TimeSpan OverallTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public int MaximumDepth { get; set; } = 4;
    public int MaximumRequests { get; set; } = 32;
}

public sealed record DefinitionGraphNode(ProcessDefinitionId Id, SagaDefinition Definition, DefinitionOrigin Origin);
public sealed record DefinitionGraphEdge(ProcessDefinitionId Source, string TargetDomain, string TargetProcess, string? RequestedVersion, string Endpoint, ProcessDefinitionId? Target);
public sealed record DiscoveryFailure(ProcessDefinitionId Source, string TargetDomain, string TargetProcess, DiscoveryFailureKind Kind, string Message);
public sealed record DefinitionGraph(IReadOnlyList<DefinitionGraphNode> Nodes, IReadOnlyList<DefinitionGraphEdge> Edges, IReadOnlyList<DiscoveryFailure> Failures);

/// <summary>Builds a static definition graph. Deliberately has no coordinator, bus, or state-store dependency.</summary>
public sealed class DefinitionDiscovery
{
    private readonly IRemoteDefinitionClient _client;
    private readonly HashSet<string> _allowed;
    private readonly bool _allowHttp;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _overallTimeout;
    private readonly int _maximumDepth;
    private readonly int _maximumRequests;

    public DefinitionDiscovery(IRemoteDefinitionClient client, DefinitionDiscoveryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout > TimeSpan.FromMinutes(1)
            || options.OverallTimeout <= TimeSpan.Zero || options.OverallTimeout > TimeSpan.FromMinutes(5)
            || options.MaximumDepth is < 1 or > 16 || options.MaximumRequests is < 1 or > 256)
            throw new ArgumentException("Discovery request timeout must be (0, 1 minute], overall timeout (0, 5 minutes], depth 1..16 and requests 1..256.", nameof(options));
        _allowed = new(StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in options.AllowedEndpoints)
        {
            if (!ProcessDefinitionValidator.TryEndpoint(endpoint, out var uri))
                throw new ArgumentException($"Invalid allowed discovery origin '{endpoint}'.", nameof(options));
            if (uri.Scheme == "http" && !options.AllowInsecureHttp)
                throw new ArgumentException("HTTP discovery origins require explicit AllowInsecureHttp for the local PoC.", nameof(options));
            _allowed.Add(uri.GetLeftPart(UriPartial.Authority));
        }
        _client = client;
        _allowHttp = options.AllowInsecureHttp;
        _timeout = options.RequestTimeout;
        _overallTimeout = options.OverallTimeout;
        _maximumDepth = options.MaximumDepth;
        _maximumRequests = options.MaximumRequests;
    }

    public async Task<DefinitionGraph> DiscoverAsync(SagaDefinition local, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var elapsed = Stopwatch.StartNew();
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overall.CancelAfter(_overallTimeout);
        new ProcessDefinitionValidator().Validate(local);
        var root = DefinitionSnapshot.Copy(local);
        var rootId = ProcessDefinitionId.From(root);
        var nodes = new Dictionary<ProcessDefinitionId, DefinitionGraphNode> { [rootId] = new(rootId, root, DefinitionOrigin.Local) };
        var edges = new List<DefinitionGraphEdge>();
        var failures = new List<DiscoveryFailure>();
        var active = new HashSet<ProcessDefinitionId>();
        var responses = new Dictionary<(string Endpoint, string Domain, string Name, string? Version), SagaDefinition>();
        var requests = 0;
        var overallBudgetExceeded = false;
        bool BudgetExpired() => overall.IsCancellationRequested || elapsed.Elapsed >= _overallTimeout;

        async Task Visit(SagaDefinition definition, int depth)
        {
            var id = ProcessDefinitionId.From(definition);
            active.Add(id);
            foreach (var source in definition.BigPicture)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (overallBudgetExceeded) break;
                ProcessDefinitionId? target = null;
                void Fail(DiscoveryFailureKind kind, string message) => failures.Add(new(id, source.Domain, source.Process, kind, message));
                try
                {
                    if (BudgetExpired()) throw new OperationCanceledException(overall.Token);
                    var uri = new Uri(source.Endpoint);
                    var origin = uri.GetLeftPart(UriPartial.Authority);
                    if (!_allowed.Contains(origin) || (uri.Scheme == "http" && !_allowHttp))
                        throw new RemoteDefinitionException(DiscoveryFailureKind.UntrustedEndpoint, "Endpoint origin is not explicitly trusted by this host.");
                    var ancestor = active.FirstOrDefault(a => a.Domain == source.Domain && a.Name == source.Process && (source.Version is null || source.Version == a.Version));
                    if (ancestor is not null)
                    {
                        target = ancestor;
                        throw new RemoteDefinitionException(DiscoveryFailureKind.Cycle, "Reference returns to an active discovery ancestor.");
                    }
                    if (depth >= _maximumDepth)
                        throw new RemoteDefinitionException(DiscoveryFailureKind.LimitExceeded, "Maximum discovery depth reached.");
                    var key = (origin, source.Domain, source.Process, source.Version);
                    if (!responses.TryGetValue(key, out var remote))
                    {
                        if (requests >= _maximumRequests)
                            throw new RemoteDefinitionException(DiscoveryFailureKind.LimitExceeded, "Maximum discovery request count reached.");
                        requests++;
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
                        timeout.CancelAfter(_timeout);
                        if (BudgetExpired()) throw new OperationCanceledException(overall.Token);
                        var response = await _client.GetAsync(uri, source, _timeout, timeout.Token).WaitAsync(timeout.Token);
                        if (BudgetExpired()) throw new OperationCanceledException(overall.Token);
                        if (response.ProtocolVersion != 1 || response.Domain != source.Domain || response.Process != source.Process
                            || !ProcessDefinitionValidator.IsVersion(response.Version)
                            || (source.Version is not null && response.Version != source.Version))
                            throw new RemoteDefinitionException(DiscoveryFailureKind.InvalidResponse, "Response protocol or requested process identity/version does not match.");
                        remote = new DefinitionYamlCodec().Parse(response.CanonicalYaml, "remote definition");
                        if (remote.Domain != response.Domain || remote.Name != response.Process || remote.Version != response.Version)
                            throw new RemoteDefinitionException(DiscoveryFailureKind.InvalidResponse, "Envelope identity differs from YAML identity.");
                        if (BudgetExpired()) throw new OperationCanceledException(overall.Token);
                        responses.Add(key, remote);
                    }
                    target = ProcessDefinitionId.From(remote);
                    if (nodes.TryGetValue(target, out var known))
                    {
                        var codec = new DefinitionYamlCodec();
                        if (codec.Serialize(known.Definition) != codec.Serialize(remote))
                            throw new RemoteDefinitionException(DiscoveryFailureKind.InvalidResponse, "Conflicting content for an already discovered process identity/version.");
                    }
                    else
                    {
                        nodes.Add(target, new(target, remote, DefinitionOrigin.Remote));
                        await Visit(remote, depth + 1);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    if (BudgetExpired())
                    {
                        overallBudgetExceeded = true;
                        Fail(DiscoveryFailureKind.OverallTimeout, "Overall discovery time budget exhausted; remaining references were not requested.");
                    }
                    else Fail(DiscoveryFailureKind.Timeout, "Definition request exceeded the configured timeout.");
                }
                catch (RemoteDefinitionException ex)
                {
                    if (BudgetExpired())
                    {
                        overallBudgetExceeded = true;
                        Fail(DiscoveryFailureKind.OverallTimeout, "Overall discovery time budget exhausted; remaining references were not requested.");
                    }
                    else Fail(ex.Kind, ex.Message);
                }
                catch (DefinitionValidationException) { Fail(DiscoveryFailureKind.InvalidResponse, "Remote YAML failed structural/condition validation."); }
                finally { edges.Add(new(id, source.Domain, source.Process, source.Version, source.Endpoint, target)); }
            }
            active.Remove(id);
        }

        await Visit(root, 0);
        return new(Array.AsReadOnly(nodes.Values.ToArray()), edges.AsReadOnly(), failures.AsReadOnly());
    }
}
