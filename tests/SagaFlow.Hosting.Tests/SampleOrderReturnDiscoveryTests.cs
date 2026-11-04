using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using SagaFlow.Core.Definitions;
using SagaFlow.Hosting.Definitions;

namespace SagaFlow.Hosting.Tests;

public sealed class SampleOrderReturnDiscoveryTests
{
    [Fact]
    public async Task IndependentSampleProcessesDiscoverRelatedDefinitionsWithoutStartingSagas()
    {
        var root = FindRoot();
        var returnDirectory = Path.Combine(root, "samples", "ReturnProcess", "SagaFlow.Return");
        var orderDirectory = Path.Combine(root, "samples", "OrderProcess", "SagaFlow.Order");
        await using var returns = await SampleProcess.StartAsync(returnDirectory, "SagaFlow.Return",
            "--SagaFlow:PublishDefinitions", "true");
        var yaml = await File.ReadAllTextAsync(Path.Combine(orderDirectory, "saga", "order-process.yaml"));
        Assert.Contains("https://localhost:65305", yaml);
        var definitionDirectory = Directory.CreateTempSubdirectory("sagaflow-order-return-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(definitionDirectory, "order-process.yaml"),
                yaml.Replace("https://localhost:65305", returns.Endpoint, StringComparison.Ordinal));
            await using var orders = await SampleProcess.StartSecureAsync(orderDirectory, "SagaFlow.Order",
                "--SagaFlow:DefinitionDirectory", definitionDirectory,
                "--SagaFlow:PublishDefinitions", "true",
                "--SagaFlow:PublishBigPictureSources", "true",
                "--SagaFlow:Discovery:AllowInsecureHttp", "true",
                "--SagaFlow:Discovery:AllowedEndpoints:0", returns.Endpoint);
            Assert.NotEqual(orders.ProcessId, returns.ProcessId);

            using var handler = new HttpClientHandler { UseCookies = true,
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
            using var client = new HttpClient(handler);
            var response = await client.GetAsync($"{orders.Endpoint}/sagaflow/api/definition-graph?domain=order&process=order-process&version=1.0");
            response.EnsureSuccessStatusCode();
            using var graph = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(2, graph.RootElement.GetProperty("nodes").GetArrayLength());
            Assert.Equal("related", graph.RootElement.GetProperty("edges")[0].GetProperty("relationship").GetString());
            Assert.Equal("return", graph.RootElement.GetProperty("edges")[0].GetProperty("target").GetProperty("domain").GetString());
            Assert.Empty(graph.RootElement.GetProperty("failures").EnumerateArray());

            var remote = await new GrpcRemoteDefinitionClient().GetAsync(new(returns.Endpoint),
                new BigPictureSourceDefinition { Domain = "return", Process = "return-process", Version = "1.0", Endpoint = returns.Endpoint },
                TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.Equal(("return", "return-process", "1.0"), (remote.Domain, remote.Process, remote.Version));
            Assert.Equal("[]", (await client.GetStringAsync($"{orders.Endpoint}/sagaflow/api/instances")).Trim());
            using var returnClient = new HttpClient { DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact };
            Assert.Equal("[]", (await returnClient.GetStringAsync($"{returns.Endpoint}/sagaflow/api/instances")).Trim());
        }
        finally { Directory.Delete(definitionDirectory, true); }
    }

    [Fact]
    public async Task NormalOrderProfileShowsLocalRelationshipWithoutPublishingItOrRequiringLogin()
    {
        var root = FindRoot();
        var returnDirectory = Path.Combine(root, "samples", "ReturnProcess", "SagaFlow.Return");
        var orderDirectory = Path.Combine(root, "samples", "OrderProcess", "SagaFlow.Order");
        await using var returns = await SampleProcess.StartAsync(returnDirectory, "SagaFlow.Return",
            "--SagaFlow:PublishDefinitions", "true");
        var yaml = await File.ReadAllTextAsync(Path.Combine(orderDirectory, "saga", "order-process.yaml"));
        var definitionDirectory = Directory.CreateTempSubdirectory("sagaflow-local-dashboard-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(definitionDirectory, "order-process.yaml"),
                yaml.Replace("https://localhost:65305", returns.Endpoint, StringComparison.Ordinal));
            await using var orders = await SampleProcess.StartSecureAsync(orderDirectory, "SagaFlow.Order",
                "--SagaFlow:DefinitionDirectory", definitionDirectory,
                "--SagaFlow:PublishDefinitions", "false",
                "--SagaFlow:Discovery:AllowInsecureHttp", "true",
                "--SagaFlow:Discovery:AllowedEndpoints:0", returns.Endpoint);
            using var handler = new HttpClientHandler { UseCookies = true,
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
            using var client = new HttpClient(handler);
            var definitionsUrl = $"{orders.Endpoint}/sagaflow/api/definitions";
            var graphUrl = $"{orders.Endpoint}/sagaflow/api/definition-graph?domain=order&process=order-process&version=1.0";
            var definitions = await client.GetStringAsync(definitionsUrl);
            Assert.Contains("related", definitions);
            Assert.DoesNotContain(returns.Endpoint, definitions);
            var graph = await client.GetStringAsync(graphUrl);
            using var document = JsonDocument.Parse(graph);
            Assert.Equal(2, document.RootElement.GetProperty("nodes").GetArrayLength());
            Assert.Equal("related", document.RootElement.GetProperty("edges")[0].GetProperty("relationship").GetString());
            Assert.Empty(document.RootElement.GetProperty("failures").EnumerateArray());
            Assert.DoesNotContain(returns.Endpoint, graph);

            await returns.DisposeAsync();
            using var unresolved = JsonDocument.Parse(await client.GetStringAsync(graphUrl));
            Assert.Null(unresolved.RootElement.GetProperty("edges")[0].GetProperty("target").GetString());
            Assert.NotEmpty(unresolved.RootElement.GetProperty("failures").EnumerateArray());
            Assert.False(string.IsNullOrWhiteSpace(unresolved.RootElement.GetProperty("failures")[0]
                .GetProperty("message").GetString()));
            await using var restartedReturns = await SampleProcess.StartAsync(returnDirectory, "SagaFlow.Return",
                "--SagaFlow:PublishDefinitions", "true", "--urls", returns.Endpoint);
            using var rediscovered = JsonDocument.Parse(await client.GetStringAsync(graphUrl));
            Assert.Equal("return", rediscovered.RootElement.GetProperty("edges")[0]
                .GetProperty("target").GetProperty("domain").GetString());
            Assert.Empty(rediscovered.RootElement.GetProperty("failures").EnumerateArray());
        }
        finally { Directory.Delete(definitionDirectory, true); }
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "SagaFlow.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("SagaFlow.slnx was not found.");
    }

    private sealed class SampleProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly TaskCompletionSource<string> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly StringBuilder _output = new();
        private Task _stdout = Task.CompletedTask;
        private Task _stderr = Task.CompletedTask;
        private bool _started;
        private bool _disposed;
        public string Endpoint { get; private set; } = "";
        public int ProcessId => _process.Id;

        private SampleProcess(string directory, string name, string[] args, bool secure = false)
        {
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
            var isolated = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", name,
                new DirectoryInfo(AppContext.BaseDirectory).Name, name + ".dll"));
            var binary = File.Exists(isolated) ? isolated : Path.Combine(directory, "bin", configuration, "net10.0", name + ".dll");
            if (!File.Exists(binary)) throw new FileNotFoundException("Build the sample projects before this test.", binary);
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            if (secure) start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            foreach (var argument in new[] { binary, "--urls", secure ? "https://127.0.0.1:0" : "http://127.0.0.1:0",
                "--Kestrel:EndpointDefaults:Protocols", secure ? "Http1AndHttp2" : "Http2", "--RabbitMq:Enabled", "false",
                "--Logging:LogLevel:Microsoft.Hosting.Lifetime", "Information" }.Concat(args))
                start.ArgumentList.Add(argument);
            _process = new Process { StartInfo = start, EnableRaisingEvents = true };
            _process.Exited += (_, _) => _ready.TrySetException(new InvalidOperationException("Sample process exited before readiness."));
        }

        public static async Task<SampleProcess> StartAsync(string directory, string name, params string[] args)
        {
            var sample = new SampleProcess(directory, name, args);
            try
            {
                sample._started = sample._process.Start();
                if (!sample._started) throw new InvalidOperationException("Could not start sample process.");
                sample._stdout = sample.ReadLines(sample._process.StandardOutput, true);
                sample._stderr = sample.ReadLines(sample._process.StandardError, false);
                sample.Endpoint = await sample._ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
                return sample;
            }
            catch (Exception error)
            {
                await sample.DisposeAsync();
                throw new InvalidOperationException($"{name} startup failed: {sample._output}", error);
            }
        }

        public static async Task<SampleProcess> StartSecureAsync(string directory, string name, params string[] args)
        {
            var sample = new SampleProcess(directory, name, args, secure: true);
            try
            {
                sample._started = sample._process.Start();
                if (!sample._started) throw new InvalidOperationException("Could not start sample process.");
                sample._stdout = sample.ReadLines(sample._process.StandardOutput, true);
                sample._stderr = sample.ReadLines(sample._process.StandardError, false);
                sample.Endpoint = await sample._ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
                return sample;
            }
            catch (Exception error)
            {
                await sample.DisposeAsync();
                throw new InvalidOperationException($"{name} startup failed: {sample._output}", error);
            }
        }

        private async Task ReadLines(StreamReader reader, bool detectEndpoint)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                lock (_output) _output.AppendLine(line);
                const string marker = "Now listening on: ";
                var index = line.IndexOf(marker, StringComparison.Ordinal);
                if (detectEndpoint && index >= 0 && Uri.TryCreate(line[(index + marker.Length)..].Trim(), UriKind.Absolute, out var uri)
                    && uri.Port > 0) _ready.TrySetResult(uri.GetLeftPart(UriPartial.Authority));
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            if (_started && !_process.HasExited) _process.Kill(entireProcessTree: true);
            if (_started) await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(_stdout, _stderr).WaitAsync(TimeSpan.FromSeconds(5));
            _process.Dispose();
        }
    }
}
