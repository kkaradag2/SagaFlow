using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace SagaFlow.Samples.Tests;

/// <summary>Runs the PostgreSQL sample test host in its own OS process and captures startup diagnostics.</summary>
internal sealed class SampleDomainProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _stdout;
    private readonly Task<string> _stderr;

    private SampleDomainProcess(Process process, string endpoint)
    {
        _process = process;
        _stdout = process.StandardOutput.ReadToEndAsync();
        _stderr = process.StandardError.ReadToEndAsync();
        Client = new HttpClient { BaseAddress = new Uri(endpoint), Timeout = TimeSpan.FromSeconds(10) };
    }

    internal HttpClient Client { get; }
    internal int Id => _process.Id;

    internal static SampleDomainProcess Start(string domain, string endpoint, IReadOnlyDictionary<string, string?> settings)
    {
        var root = FindRoot();
        var name = $"SagaFlow.{domain}";
        var directory = Path.Combine(root, "samples", $"{domain}Process", name);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(PostgreSqlDomainHost).Assembly.Location);
        start.ArgumentList.Add(domain);
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["Kestrel__Endpoints__Api__Url"] = endpoint;
        start.Environment["Kestrel__Endpoints__Api__Protocols"] = "Http1";
        foreach (var (key, value) in settings)
        {
            if (value is null) start.Environment.Remove(key);
            else start.Environment[key] = value;
        }
        return new SampleDomainProcess(Process.Start(start) ?? throw new InvalidOperationException($"Could not start {name}."), endpoint);
    }

    internal async Task WaitForStartupAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!deadline.IsCancellationRequested)
        {
            if (_process.HasExited)
                throw new InvalidOperationException($"Sample host exited before startup. {await ReadOutputAsync()}");
            try
            {
                using var response = await Client.GetAsync("/sagaflow/api/definitions", deadline.Token);
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            await Task.Delay(100, deadline.Token);
        }
        throw new TimeoutException($"Sample host did not become ready at {Client.BaseAddress}.");
    }

    internal async Task<string> WaitForStartupFailureAsync()
    {
        await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.NotEqual(0, _process.ExitCode);
        return await ReadOutputAsync();
    }

    private async Task<string> ReadOutputAsync() => string.Join(Environment.NewLine, await Task.WhenAll(_stdout, _stderr));

    internal static string[] AllocateEndpoints(int count)
    {
        var listeners = Enumerable.Range(0, count).Select(_ => new TcpListener(IPAddress.Loopback, 0)).ToArray();
        try
        {
            foreach (var listener in listeners) listener.Start();
            return listeners.Select(listener => $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}").ToArray();
        }
        finally
        {
            foreach (var listener in listeners) listener.Stop();
        }
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "SagaFlow.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("SagaFlow.slnx was not found.");
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await ReadOutputAsync().WaitAsync(TimeSpan.FromSeconds(10));
        _process.Dispose();
    }
}
