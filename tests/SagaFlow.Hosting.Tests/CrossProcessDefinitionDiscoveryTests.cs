using System.Diagnostics;
using System.Text;
using SagaFlow.Core.Definitions;
using SagaFlow.Engine.Discovery;
using SagaFlow.Engine.ProcessDefinition;
using SagaFlow.Hosting.Definitions;

namespace SagaFlow.Hosting.Tests;

public sealed class CrossProcessDefinitionDiscoveryTests
{
    [Fact]
    public async Task SeparatePublisherProcessPreservesContractOwnershipAndBigPictureRelationship()
    {
        await using var catalog = await DefinitionDiscoveryIntegrationTests.DomainHost.Start("fulfillment", "fulfillment-process");
        var directory = Directory.CreateTempSubdirectory("sagaflow-cross-process-").FullName;
        try
        {
            var published = new SagaDefinition
            {
                Domain = "order", Name = "purchase-process", Version = "1.0",
                Trigger = new() { Event = "OrderCreated" }, InitialStep = "payment",
                Steps = [new() { Name = "payment", Command = "AuthorizePayment", Transitions = [new() { Event = "success", NextStep = "completed" }] }],
                BigPicture = [new() { Domain = "fulfillment", Process = "fulfillment-process", Version = "1.0", Endpoint = catalog.Endpoint }]
            };
            await File.WriteAllTextAsync(Path.Combine(directory, "purchase.yaml"), new DefinitionYamlCodec().Serialize(published));
            await using var publisher = await PublisherProcess.StartAsync(directory);
            Assert.NotEqual(Environment.ProcessId, publisher.Id);

            var source = new BigPictureSourceDefinition { Domain = "order", Process = "purchase-process", Version = "1.0", Endpoint = publisher.Endpoint };
            var transport = new GrpcRemoteDefinitionClient();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var envelope = await transport.GetAsync(new(publisher.Endpoint), source, TimeSpan.FromSeconds(5), deadline.Token);
            Assert.Equal(1, envelope.ProtocolVersion);
            Assert.Equal(("order", "purchase-process", "1.0"), (envelope.Domain, envelope.Process, envelope.Version));
            var retrieved = new DefinitionYamlCodec().Parse(envelope.CanonicalYaml);
            Assert.Equal("OrderCreated", retrieved.Trigger.Event);
            Assert.Equal("payment", retrieved.InitialStep);
            var relationship = Assert.Single(retrieved.BigPicture);
            Assert.Equal(("fulfillment", "fulfillment-process", "1.0", catalog.Endpoint),
                (relationship.Domain, relationship.Process, relationship.Version, relationship.Endpoint));

            var root = new SagaDefinition
            {
                Domain = "visibility", Name = "visibility-process", Version = "1.0", Trigger = new() { Event = "Requested" }, InitialStep = "view",
                Steps = [new() { Name = "view", Command = "View", Transitions = [new() { Event = "success", NextStep = "completed" }] }], BigPicture = [source]
            };
            var registry = new ProcessDefinitionRegistry();
            registry.RegisterLocalBatch([root]);
            var options = new DefinitionDiscoveryOptions { AllowInsecureHttp = true, OverallTimeout = TimeSpan.FromSeconds(10) };
            options.AllowedEndpoints.Add(publisher.Endpoint);
            options.AllowedEndpoints.Add(catalog.Endpoint);
            var graph = await new DefinitionDiscovery(transport, options).DiscoverAsync(registry.Find(ProcessDefinitionId.From(root))!.Definition, deadline.Token);
            Assert.Empty(graph.Failures);
            Assert.Equal(3, graph.Nodes.Count);
            Assert.Equal(2, graph.Edges.Count);
            Assert.Contains(graph.Nodes, n => n.Id == new ProcessDefinitionId("order", "purchase-process", "1.0") && n.Origin == DefinitionOrigin.Remote);
            Assert.Contains(graph.Edges, edge => edge.Source == ProcessDefinitionId.From(published)
                && edge.Target == new ProcessDefinitionId("fulfillment", "fulfillment-process", "1.0"));
            Assert.Single(registry.Snapshot());
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>Runs the existing sample, captures readiness, and kills the process tree on every exit path.</summary>
    private sealed class PublisherProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly TaskCompletionSource<string> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly StringBuilder _logs = new();
        private Task _stdout = Task.CompletedTask;
        private Task _stderr = Task.CompletedTask;
        private bool _started;
        private bool _disposed;
        public string Endpoint { get; private set; } = "";
        public int Id => _process.Id;

        private PublisherProcess(string directory)
        {
            var host = Path.Combine(AppContext.BaseDirectory, "DiscoveryProcessFixture", "SagaFlow.SimpleShopping.dll");
            if (!File.Exists(host)) throw new FileNotFoundException("Build the hosting tests to copy the sample process fixture.", host);
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                WorkingDirectory = Path.GetDirectoryName(host)!, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            foreach (var argument in new[] { host, "--urls", "http://127.0.0.1:0", "--Kestrel:EndpointDefaults:Protocols", "Http2",
                "--SagaFlow:PublishDefinitions", "true", "--SagaFlow:PublishBigPictureSources", "true",
                "--SagaFlow:DefinitionDirectory", directory, "--Logging:LogLevel:Microsoft.Hosting.Lifetime", "Information" }) start.ArgumentList.Add(argument);
            _process = new() { StartInfo = start, EnableRaisingEvents = true };
            _process.Exited += (_, _) => _ready.TrySetException(new InvalidOperationException("Publisher exited before reporting readiness."));
        }

        public static async Task<PublisherProcess> StartAsync(string directory)
        {
            var child = new PublisherProcess(directory);
            try
            {
                child._started = child._process.Start();
                if (!child._started) throw new InvalidOperationException("Publisher process did not start.");
                child._stdout = child.ReadOutput(child._process.StandardOutput, detectEndpoint: true);
                child._stderr = child.ReadOutput(child._process.StandardError, detectEndpoint: false);
                child.Endpoint = await child._ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
                return child;
            }
            catch (Exception error)
            {
                await child.DisposeAsync();
                throw new InvalidOperationException($"Publisher startup failed. {child._logs}", error);
            }
        }

        private async Task ReadOutput(StreamReader reader, bool detectEndpoint)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                lock (_logs) _logs.AppendLine(line);
                const string marker = "Now listening on: ";
                var start = line.IndexOf(marker, StringComparison.Ordinal);
                if (detectEndpoint && start >= 0 && Uri.TryCreate(line[(start + marker.Length)..].Trim(), UriKind.Absolute, out var uri)
                    && uri.Host == "127.0.0.1" && uri.Port > 0) _ready.TrySetResult(uri.GetLeftPart(UriPartial.Authority));
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (!_started) return;
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await Task.WhenAll(_stdout, _stderr).WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { _process.Dispose(); }
        }
    }
}
