using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Npgsql;
using RabbitMQ.Client;
using SagaFlow.Storage.PostgreSQL.Tests;

namespace SagaFlow.Samples.Tests;

internal sealed class SampleTestEnvironment : IAsyncDisposable
{
    private static readonly string[] Domains = ["Order", "Return", "Procurement"];
    private static readonly string[] Participants = ["Payment", "Inventory", "Shipment", "ManagerApproval", "Warehouse"];
    private static readonly string[] Commands = ["payment", "inventory", "shipment", "managerApproval", "warehouse",
        "notification", "acceptedNotification", "rejectedNotification"];
    private static readonly string[] Outcomes = ["order.outcomes", "return.outcomes", "procurement.outcomes"];
    private static readonly string Root = Path.Combine(Path.GetFullPath(Metadata("RepositoryRoot")), "Sample");
    private readonly DirectoryInfo _definitions = Directory.CreateTempSubdirectory("sagaflow-sample-test-");
    private readonly Dictionary<string, PostgreSqlTestDatabase> _databases = new();
    private readonly Dictionary<string, RunningSample> _processes = new();
    private readonly Dictionary<string, string> _http = new();
    private readonly Dictionary<string, string> _discovery = new();
    private readonly string _rabbit = Environment.GetEnvironmentVariable("ConnectionStrings__RabbitMq")!;
    private IConnection? _connection;
    private IChannel? _channel;

    internal HttpClient Http { get; } = new() { Timeout = TimeSpan.FromSeconds(5) };
    internal IChannel Channel => _channel!;
    internal string Url(string service, string path) => _http[service] + path;

    internal async Task StartAsync()
    {
        SampleRabbitMqTestIsolation.RequireVirtualHost();
        var endpoints = AllocateEndpoints(8);
        for (var i = 0; i < Domains.Length; i++)
        {
            _http.Add(Domains[i], endpoints[i]);
            _discovery.Add(Domains[i], endpoints[i + Domains.Length]);
        }
        _http.Add("ManagerApproval", endpoints[6]);
        _http.Add("Warehouse", endpoints[7]);

        await Task.WhenAll(Domains.Concat(Participants).Select(async name =>
        {
            PostgreSqlTestDatabase database;
            lock (_databases) _databases.Add(name, database = new());
            await database.CreateAsync();
            var file = name == "ManagerApproval" ? "manager-approval" : name.ToLowerInvariant();
            var script = await File.ReadAllTextAsync(Path.Combine(Root, "infrastructure", "postgres", $"init-{file}.sql"));
            // The prefix is psql database-selection syntax. PostgreSqlTestDatabase already created an isolated database.
            await PostgreSqlTestDatabase.ExecuteAsync(database.ConnectionString, script[script.IndexOf("BEGIN;", StringComparison.Ordinal)..]);
        }));

        _connection = await new ConnectionFactory { Uri = new Uri(_rabbit) }.CreateConnectionAsync();
        _channel = await _connection.CreateChannelAsync(new CreateChannelOptions(true, true));
        foreach (var queue in Commands.Concat(Outcomes))
            await Channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false);

        foreach (var name in Domains.Concat(Participants)) Launch(name);
        var python = Environment.GetEnvironmentVariable("SAGAFLOW_SAMPLE_PYTHON") ?? "python";
        var notification = Path.Combine(Root, "Participants", "Notification");
        _processes.Add("Notification", new RunningSample(python, notification, ["-u", "main.py"],
            new() { ["SAGAFLOW_NOTIFICATION_AMQP_URL"] = _rabbit, ["PYTHONDONTWRITEBYTECODE"] = "1" }));

        await EventuallyAsync(async () =>
        {
            foreach (var queue in Commands.Concat(Outcomes))
                if (await Channel.ConsumerCountAsync(queue) != 1) return false;
            return true;
        });
    }

    private void Launch(string name, bool publish = true)
    {
        var domainStarter = Domains.Contains(name);
        var group = domainStarter ? "DomainStarters" : "Participants";
        var assembly = $"Sample.{group}.{name}";
        var directory = Path.Combine(Root, group, name);
        var isolated = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", assembly,
            new DirectoryInfo(AppContext.BaseDirectory).Name, $"{assembly}.dll"));
        var binary = File.Exists(isolated) ? isolated : Path.Combine(directory, "bin", Metadata("BuildConfiguration"), "net10.0", $"{assembly}.dll");
        if (!File.Exists(binary)) throw new FileNotFoundException($"Build {assembly} before running the sample tests.", binary);

        var settings = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["DOTNET_ENVIRONMENT"] = "Development",
            [$"ConnectionStrings__{name}"] = _databases[name].ConnectionString,
            ["ConnectionStrings__RabbitMq"] = _rabbit,
            ["RabbitMq__ConnectionString"] = _rabbit
        };
        if (_http.TryGetValue(name, out var endpoint)) settings["Kestrel__Endpoints__Http__Url"] = endpoint;
        if (domainStarter)
        {
            settings["Kestrel__Endpoints__Discovery__Url"] = _discovery[name];
            settings["SagaFlow__Publication__Enabled"] = publish ? "true" : "false";
            var yaml = $"{name.ToLowerInvariant()}-process.yaml";
            var definition = File.ReadAllText(Path.Combine(directory, "saga", yaml))
                .Replace("http://127.0.0.1:5189", _discovery["Order"], StringComparison.Ordinal)
                .Replace("http://127.0.0.1:5188", _discovery["Return"], StringComparison.Ordinal)
                .Replace("http://127.0.0.1:5288", _discovery["Procurement"], StringComparison.Ordinal);
            var path = Path.Combine(_definitions.FullName, yaml);
            File.WriteAllText(path, definition);
            settings["SagaFlow__DefinitionPath"] = path;
        }
        _processes.Add(name, new RunningSample(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            Path.GetDirectoryName(binary)!, [binary], settings));
    }

    internal async Task RestartAsync(string name, bool publish = true)
    {
        await StopAsync(name);
        Launch(name, publish);
        await EventuallyAsync(async () =>
        {
            using var response = await Http.GetAsync(Url(name, "/swagger/v1/swagger.json"));
            return response.IsSuccessStatusCode;
        });
    }

    internal async Task StopAsync(string name)
    {
        if (_processes.Remove(name, out var process)) await process.DisposeAsync();
    }

    internal void StartDomain(string name, bool publish = true) => Launch(name, publish);

    internal async Task<T> QueryAsync<T>(string name, string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(_databases[name].ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in parameters) command.Parameters.Add(new NpgsqlParameter { Value = value });
        return (T)(await command.ExecuteScalarAsync())!;
    }

    internal async Task EventuallyAsync(Func<Task<bool>> assertion, string? description = null)
    {
        for (var attempt = 0; attempt < 160; attempt++)
        {
            foreach (var (name, process) in _processes)
                if (process.HasExited) throw new InvalidOperationException($"{name} exited: {await process.OutputAsync()}");
            try { if (await assertion()) return; }
            catch (HttpRequestException) { }
            await Task.Delay(125);
        }
        throw new TimeoutException(description ?? "Sample did not reach the expected state.");
    }

    internal static string DefinitionPath(string domain)
        => Path.Combine(Root, "DomainStarters", domain, "saga", $"{domain.ToLowerInvariant()}-process.yaml");

    private static string Metadata(string key) => typeof(SampleTestEnvironment).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(attribute => attribute.Key == key).Value!;

    private static string[] AllocateEndpoints(int count)
    {
        var listeners = Enumerable.Range(0, count).Select(_ => new TcpListener(IPAddress.Loopback, 0)).ToArray();
        try
        {
            foreach (var listener in listeners) listener.Start();
            return listeners.Select(listener => $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}").ToArray();
        }
        finally { foreach (var listener in listeners) listener.Stop(); }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var process in _processes.Values) await process.DisposeAsync();
        if (_channel is not null) await _channel.DisposeAsync();
        if (_connection is not null) await _connection.DisposeAsync();
        foreach (var database in _databases.Values) await database.DisposeAsync();
        _definitions.Delete(recursive: true);
        Http.Dispose();
    }

    private sealed class RunningSample : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _stdout;
        private readonly Task<string> _stderr;

        internal RunningSample(string executable, string directory, string[] arguments, Dictionary<string, string> settings)
        {
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            foreach (var (key, value) in settings) start.Environment[key] = value;
            _process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {executable}.");
            _stdout = _process.StandardOutput.ReadToEndAsync();
            _stderr = _process.StandardError.ReadToEndAsync();
        }

        internal bool HasExited => _process.HasExited;
        internal async Task<string> OutputAsync() => string.Join(Environment.NewLine, await Task.WhenAll(_stdout, _stderr));

        public async ValueTask DisposeAsync()
        {
            if (!HasExited) _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await OutputAsync().WaitAsync(TimeSpan.FromSeconds(5));
            _process.Dispose();
        }
    }
}
