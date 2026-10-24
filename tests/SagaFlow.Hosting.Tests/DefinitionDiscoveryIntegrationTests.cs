using System.Net;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SagaFlow.Core.Definitions;
using SagaFlow.Core.Abstractions;
using SagaFlow.Engine.Coordination;
using SagaFlow.Engine.Discovery;
using SagaFlow.Engine.ProcessDefinition;
using SagaFlow.Hosting.DependencyInjection;
using SagaFlow.Hosting.Endpoints;
using SagaFlow.Hosting.Grpc.V1;

namespace SagaFlow.Hosting.Tests;

public sealed class DefinitionDiscoveryIntegrationTests
{
    [Fact]
    public async Task ThreeDomainsLoadTheirYamlAndDiscoverDefinitionGraphOverRealGrpc()
    {
        await using var b = await DomainHost.Start("payment", "payment-process");
        await using var c = await DomainHost.Start("fulfillment", "fulfillment-process");
        await using var a = await DomainHost.Start("order", "order-process", references: [Source(b, "payment", "payment-process"), Source(c, "fulfillment", "fulfillment-process")]);
        // No coordinator can be resolved in these fixtures; discovery still works through HTTP/2.
        Assert.Null(a.App.Services.GetService<SagaCoordinator>());
        var registry = a.App.Services.GetRequiredService<ProcessDefinitionRegistry>();
        var root = registry.Find(new("order", "order-process", "1.0"))!.Definition;
        var graph = await a.App.Services.GetRequiredService<DefinitionDiscovery>().DiscoverAsync(root);
        Assert.Empty(graph.Failures);
        Assert.Equal(3, graph.Nodes.Count);
        Assert.Equal(2, graph.Edges.Count);
        Assert.All(graph.Edges, edge => Assert.NotNull(edge.Target));
        Assert.Equal(2, graph.Nodes.Count(n => n.Origin == DefinitionOrigin.Remote));
        Assert.All(graph.Nodes, node =>
        {
            Assert.Equal("Created", node.Definition.Trigger.Event);
            Assert.Equal("work", node.Definition.InitialStep);
        });
        Assert.Single(registry.Snapshot());
        Assert.All(b.App.Services.GetRequiredService<ProcessDefinitionRegistry>().Snapshot(), d => Assert.Equal(DefinitionOrigin.Local, d.Origin));
        Assert.Equal("fulfillment", c.App.Services.GetRequiredService<ProcessDefinitionRegistry>().Snapshot().Single().Definition.Domain);
    }

    [Fact]
    public async Task PublicationListsOnlyAllowedLocalDefinitionsAndRedactsTopologyByDefault()
    {
        await using var host = await DomainHost.Start("payment", "payment-process", references: [new() { Domain = "private", Process = "private-process", Endpoint = "https://private-internal:7001" }]);
        var registry = host.App.Services.GetRequiredService<ProcessDefinitionRegistry>();
        registry.RegisterRemote(Definition("remote", "remote-process"), "https://remote");
        registry.RegisterLocalBatch([Definition("payment", "private-process")]);
        using var channel = GrpcChannel.ForAddress(host.Endpoint);
        var client = new ProcessDefinitions.ProcessDefinitionsClient(channel);
        var list = await client.ListDefinitionsAsync(new());
        Assert.Equal(1u, list.ProtocolVersion);
        Assert.Equal("payment-process", list.Definitions.Single().Process);
        var response = await client.GetDefinitionAsync(new() { Domain = "payment", Process = "payment-process", Version = "1.0" });
        Assert.Empty(new DefinitionYamlCodec().Parse(response.CanonicalYaml).BigPicture);
        var published = new DefinitionYamlCodec().Parse(response.CanonicalYaml);
        Assert.Equal("Created", published.Trigger.Event);
        Assert.Equal("work", published.InitialStep);
        Assert.DoesNotContain("private-internal", response.CanonicalYaml);
        Assert.DoesNotContain(host.Directory, response.CanonicalYaml);
        var ex = await Assert.ThrowsAsync<RpcException>(async () => await client.GetDefinitionAsync(new() { Domain = "payment", Process = "private-process" }));
        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task UnknownVersionAndUnversionedAmbiguityAreExplicit()
    {
        await using var host = await DomainHost.Start("payment", "payment-process", versions: ["1.0", "2.0"]);
        using var channel = GrpcChannel.ForAddress(host.Endpoint);
        var client = new ProcessDefinitions.ProcessDefinitionsClient(channel);
        foreach (var request in new[]
        {
            new GetDefinitionRequest { Domain = "payment", Process = "missing" },
            new GetDefinitionRequest { Domain = "payment", Process = "payment-process", Version = "3.0" }
        })
            Assert.Equal(StatusCode.NotFound, (await Assert.ThrowsAsync<RpcException>(async () => await client.GetDefinitionAsync(request))).StatusCode);
        Assert.Equal(StatusCode.FailedPrecondition, (await Assert.ThrowsAsync<RpcException>(async () => await client.GetDefinitionAsync(new() { Domain = "payment", Process = "payment-process" }))).StatusCode);
        var pinned = await client.GetDefinitionAsync(new() { Domain = "payment", Process = "payment-process", Version = "2.0" });
        Assert.Equal("2.0", pinned.Identity.Version);
        Assert.Equal(StatusCode.InvalidArgument, (await Assert.ThrowsAsync<RpcException>(async () => await client.GetDefinitionAsync(new() { Domain = "../payment", Process = "payment-process" }))).StatusCode);

        var graph = await Discovery([host.Endpoint]).DiscoverAsync(Definition("order", "order-process", [Source(host, "payment", "payment-process")]));
        Assert.Equal(DiscoveryFailureKind.AmbiguousVersion, graph.Failures.Single().Kind);
    }

    [Theory]
    [InlineData("wrong-domain")]
    [InlineData("wrong-version")]
    [InlineData("invalid-yaml")]
    [InlineData("invalid-protocol")]
    [InlineData("mismatched-yaml")]
    public async Task InvalidRemoteResponsesBecomeStructuredFailures(string mode)
    {
        await using var remote = await StubHost.Start(mode);
        var discovery = Discovery([remote.Endpoint]);
        var graph = await discovery.DiscoverAsync(Definition("order", "order-process", [new() { Domain = "payment", Process = "payment-process", Version = "1.0", Endpoint = remote.Endpoint }]));
        Assert.Equal(DiscoveryFailureKind.InvalidResponse, graph.Failures.Single().Kind);
        Assert.Single(graph.Nodes);
    }

    [Fact]
    public async Task UnavailableMissingAndTimeoutFailuresDoNotCorruptSuccessfulResults()
    {
        await using var good = await DomainHost.Start("payment", "payment-process");
        await using var slow = await StubHost.Start("slow");
        await using var unavailable = await DomainHost.Start("unused", "unused-process");
        var deadEndpoint = unavailable.Endpoint;
        await unavailable.App.StopAsync();
        var root = Definition("order", "order-process",
        [
            Source(good, "payment", "payment-process"),
            Source(good, "payment", "missing-process"),
            new() { Domain = "slow", Process = "slow-process", Endpoint = slow.Endpoint },
            new() { Domain = "unused", Process = "unused-process", Endpoint = deadEndpoint }
        ]);
        var graph = await Discovery([good.Endpoint, slow.Endpoint, deadEndpoint], timeout: TimeSpan.FromMilliseconds(400)).DiscoverAsync(root);
        Assert.Equal(2, graph.Nodes.Count);
        Assert.Equal(4, graph.Edges.Count);
        Assert.Contains(graph.Failures, f => f.Kind == DiscoveryFailureKind.NotFound);
        Assert.Contains(graph.Failures, f => f.Kind == DiscoveryFailureKind.Timeout);
        Assert.Contains(graph.Failures, f => f.TargetDomain == "unused" && f.Kind is DiscoveryFailureKind.Unavailable or DiscoveryFailureKind.Timeout);
        Assert.Equal(4, root.BigPicture.Count);
    }

    [Fact]
    public async Task EndpointDenyPolicyRunsBeforeNetworkCalls()
    {
        await using var remote = await DomainHost.Start("payment", "payment-process");
        var graph = await Discovery([]).DiscoverAsync(Definition("order", "order-process", [Source(remote, "payment", "payment-process")]));
        Assert.Equal(DiscoveryFailureKind.UntrustedEndpoint, graph.Failures.Single().Kind);
        Assert.Single(graph.Nodes);
    }

    [Fact]
    public async Task GrpcUnavailableStatusIsReportedWithoutLeakingServerDetails()
    {
        await using var remote = await StubHost.Start("unavailable");
        var graph = await Discovery([remote.Endpoint]).DiscoverAsync(Definition("order", "order-process",
            [new() { Domain = "payment", Process = "payment-process", Endpoint = remote.Endpoint }]));
        Assert.Equal(DiscoveryFailureKind.Unavailable, graph.Failures.Single().Kind);
        Assert.DoesNotContain("private", graph.Failures.Single().Message);
    }

    [Fact]
    public async Task CyclesAndDepthAndRequestLimitsAreBounded()
    {
        // Root need not publish anything. The callback to it is recognized before a request.
        var back = new BigPictureSourceDefinition { Domain = "order", Process = "order-process", Endpoint = "http://127.0.0.1:1" };
        await using var b = await DomainHost.Start("payment", "payment-process", references: [back], publishTopology: true);
        var root = Definition("order", "order-process", [Source(b, "payment", "payment-process")]);
        var graph = await Discovery([b.Endpoint, back.Endpoint]).DiscoverAsync(root);
        Assert.Equal(DiscoveryFailureKind.Cycle, graph.Failures.Single().Kind);
        Assert.Equal(2, graph.Nodes.Count);
        var limited = await Discovery([b.Endpoint, back.Endpoint], depth: 1).DiscoverAsync(root);
        // Cycle detection remains the stronger explanation for this edge.
        Assert.Single(limited.Failures);
        await using var c = await DomainHost.Start("fulfillment", "fulfillment-process");
        await using var branch = await DomainHost.Start("branch", "branch-process", references: [Source(c, "fulfillment", "fulfillment-process")], publishTopology: true);
        var tree = Definition("order", "order-process", [Source(branch, "branch", "branch-process")]);
        Assert.Equal(DiscoveryFailureKind.LimitExceeded, (await Discovery([branch.Endpoint, c.Endpoint], depth: 1).DiscoverAsync(tree)).Failures.Single().Kind);
        Assert.Equal(DiscoveryFailureKind.LimitExceeded, (await Discovery([branch.Endpoint, c.Endpoint], requests: 1).DiscoverAsync(tree)).Failures.Single().Kind);
    }

    [Fact]
    public async Task OrdinaryHostLoadsLocalYamlWithoutGrpcEnabledOrRemoteAvailability()
    {
        await using var host = await DomainHost.Start("order", "order-process", publish: false,
            references: [new() { Domain = "offline", Process = "offline-process", Endpoint = "https://unreachable.invalid" }]);
        Assert.Single(host.App.Services.GetRequiredService<ProcessDefinitionRegistry>().Snapshot());
        Assert.Throws<InvalidOperationException>(() => host.App.MapSagaFlowDefinitionDiscovery());
    }

    [Fact]
    public void IncorrectPublicationAndTrustConfigurationFailClearly()
    {
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddSagaFlow(o => o.Publication.Enabled = true));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddSagaFlow(o => o.Discovery.AllowedEndpoints.Add("http://localhost:7001")));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddSagaFlow(o => o.Discovery.AllowedEndpoints.Add("https://localhost/private")));
    }

    [Fact]
    public async Task PublishingMissingLocalIdentityFailsHostStartup()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSagaFlow(o => { o.Publication.Enabled = true; o.Publication.PublishedDefinitions.Add(new("absent", "absent", "1.0")); });
        await using var app = builder.Build();
        app.MapSagaFlowDefinitionDiscovery();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());
        Assert.Contains("not loaded locally", error.Message);
    }

    [Fact]
    public async Task EnabledPublicationWithoutMappingFailsStartup()
    {
        var builder = Builder();
        builder.Services.AddSagaFlow(o => { o.Publication.Enabled = true; o.Publication.PublishedDefinitions.Add(new("order", "order-process", "1.0")); });
        await using var app = builder.Build();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());
        Assert.Contains("MapSagaFlowDefinitionDiscovery", error.Message);
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        await using var slow = await StubHost.Start("slow");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Discovery([slow.Endpoint]).DiscoverAsync(
            Definition("order", "order-process", [new() { Domain = "payment", Process = "payment-process", Endpoint = slow.Endpoint }]), cancellation.Token));
    }

    [Fact]
    public async Task OverallBudgetCancelsRealGrpcRequestAndPreservesEarlierSuccess()
    {
        await using var good = await DomainHost.Start("payment", "payment-process");
        await using var slow = await StubHost.Start("slow");
        var root = Definition("order", "order-process", [Source(good, "payment", "payment-process"),
            new() { Domain = "slow", Process = "slow-process", Endpoint = slow.Endpoint }]);
        var graph = await Discovery([good.Endpoint, slow.Endpoint], timeout: TimeSpan.FromSeconds(5), overall: TimeSpan.FromMilliseconds(600)).DiscoverAsync(root);
        Assert.Equal(2, graph.Nodes.Count);
        Assert.Equal(DiscoveryFailureKind.OverallTimeout, graph.Failures.Single().Kind);
    }

    private static BigPictureSourceDefinition Source(DomainHost host, string domain, string process) => new() { Domain = domain, Process = process, Endpoint = host.Endpoint };
    private static DefinitionDiscovery Discovery(string[] endpoints, int depth = 4, int requests = 32, TimeSpan? timeout = null, TimeSpan? overall = null)
    {
        var options = new DefinitionDiscoveryOptions { AllowInsecureHttp = true, MaximumDepth = depth, MaximumRequests = requests, RequestTimeout = timeout ?? TimeSpan.FromSeconds(3) };
        if (overall is not null) options.OverallTimeout = overall.Value;
        foreach (var endpoint in endpoints) options.AllowedEndpoints.Add(endpoint);
        return new(new SagaFlow.Hosting.Definitions.GrpcRemoteDefinitionClient(), options);
    }

    private static SagaDefinition Definition(string domain, string process, BigPictureSourceDefinition[]? sources = null, string version = "1.0") => new()
    {
        Domain = domain, Name = process, Version = version, BigPicture = sources ?? [],
        Trigger = new() { Event = "Created" }, InitialStep = "work",
        Steps = [new() { Name = "work", Command = "DoWork", Transitions = [new() { Event = "success", NextStep = "completed" }, new() { Event = "failure", NextStep = "compensation" }] }]
    };

    private static WebApplicationBuilder Builder()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http2));
        return builder;
    }

    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

    internal sealed class DomainHost(WebApplication app, string directory) : IAsyncDisposable
    {
        public WebApplication App { get; } = app;
        public string Directory { get; } = directory;
        public string Endpoint => Address(App);

        public static async Task<DomainHost> Start(string domain, string process, BigPictureSourceDefinition[]? references = null, bool publish = true, bool publishTopology = false, string[]? versions = null)
        {
            var directory = System.IO.Directory.CreateTempSubdirectory("sagaflow-grpc-").FullName;
            var builder = Builder();
            versions ??= ["1.0"];
            foreach (var version in versions)
                await File.WriteAllTextAsync(Path.Combine(directory, version + ".yaml"), new DefinitionYamlCodec().Serialize(Definition(domain, process, references, version)));
            builder.Services.AddSagaFlow(options =>
            {
                options.LoadFromDirectory(directory);
                options.Publication.Enabled = publish;
                options.Publication.IncludeBigPictureSources = publishTopology;
                foreach (var version in versions) options.Publication.PublishedDefinitions.Add(new(domain, process, version));
                options.Discovery.AllowInsecureHttp = true;
                foreach (var source in references ?? []) options.Discovery.AllowedEndpoints.Add(source.Endpoint);
            });
            builder.Services.RemoveAll<SagaCoordinator>();
            builder.Services.RemoveAll<ISagaFlow>();
            builder.Services.RemoveAll<SagaFlow.Engine.Messaging.OutcomeEventReceiver>();
            var app = builder.Build();
            if (publish) app.MapSagaFlowDefinitionDiscovery();
            await app.StartAsync();
            return new(app, directory);
        }

        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            System.IO.Directory.Delete(Directory, true);
        }
    }

    private sealed class StubHost(WebApplication app) : IAsyncDisposable
    {
        public string Endpoint => Address(app);
        public static async Task<StubHost> Start(string mode)
        {
            var builder = Builder();
            builder.Services.AddSingleton(new StubMode(mode));
            builder.Services.AddGrpc();
            var app = builder.Build();
            app.MapGrpcService<StubDefinitionsService>();
            await app.StartAsync();
            return new(app);
        }
        public ValueTask DisposeAsync() => app.DisposeAsync();
    }

    public sealed record StubMode(string Mode);
    public sealed class StubDefinitionsService(StubMode mode) : ProcessDefinitions.ProcessDefinitionsBase
    {
        public override async Task<DefinitionEnvelope> GetDefinition(GetDefinitionRequest request, ServerCallContext context)
        {
            if (mode.Mode == "unavailable") throw new RpcException(new Status(StatusCode.Unavailable, "private server details"));
            if (mode.Mode == "slow") await Task.Delay(TimeSpan.FromSeconds(10), context.CancellationToken);
            return new()
            {
                ProtocolVersion = mode.Mode == "invalid-protocol" ? 2u : 1u,
                Identity = new() { Domain = mode.Mode == "wrong-domain" ? "wrong" : request.Domain, Process = request.Process, Version = mode.Mode == "wrong-version" ? "9.0" : "1.0" },
                CanonicalYaml = mode.Mode == "invalid-yaml" ? "invalid yaml" : new DefinitionYamlCodec().Serialize(Definition(mode.Mode == "mismatched-yaml" ? "wrong" : request.Domain, request.Process))
            };
        }
    }
}
