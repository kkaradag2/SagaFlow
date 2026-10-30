using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Messages;
using SagaFlow.Core.State;
using SagaFlow.Engine.ContextState;
using SagaFlow.Engine.Coordination;
using SagaFlow.Engine.Messaging;
using SagaFlow.Engine.Persistence.InMemory;
using SagaFlow.Engine.ProcessDefinition;
using SagaFlow.Engine.Timeouts;
using SagaFlow.Hosting.DependencyInjection;
using SagaFlow.Hosting.Dashboard;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR.Client;

namespace SagaFlow.Hosting.Tests;

public sealed class LocalSagaCoordinationHostingTests
{
    [Fact]
    public async Task DomainStarterCallsOnlyStartWithPayloadAndHostLoadsYamlBeforeExecution()
    {
        await using var host = await RuntimeHost.Start();
        await using var scope = host.App.Services.CreateAsyncScope();
        var business = scope.ServiceProvider.GetRequiredService<StarterBusinessService>();
        var id = await business.SaveThenStartAsync(new { orderId = 123 });
        Assert.True(business.LocalWorkCompleted);
        var store = scope.ServiceProvider.GetRequiredService<ISagaStateStore>();
        var state = (await store.FindAsync(id))!;
        Assert.Equal(2, state.Steps.Count);
        Assert.Equal("pay", state.CurrentStep);
        Assert.Equal(123, state.Context.GetProperty("orderId").GetInt32());
        var command = Assert.Single(host.Bus.Commands);
        Assert.Equal("AuthorizePayment", command.Name);
        Assert.Equal(state.CorrelationId, command.Headers.CorrelationId);
        await scope.ServiceProvider.GetRequiredService<OutcomeEventReceiver>().ReceiveAsync(new()
        { Headers = command.Headers, IsSuccess = true, Result = JsonSerializer.SerializeToElement(new { authorized = true }) });
        Assert.Equal("ship", (await store.FindAsync(id))!.CurrentStep);
        Assert.Equal("CreateShipment", host.Bus.Commands.ToArray()[1].Name);
    }

    [Fact]
    public async Task AllRuntimeServicesResolveWithScopeValidationAndStoreIsSingleton()
    {
        await using var host = await RuntimeHost.Start();
        await using var first = host.App.Services.CreateAsyncScope();
        await using var second = host.App.Services.CreateAsyncScope();
        Assert.Same(first.ServiceProvider.GetRequiredService<ISagaStateStore>(), second.ServiceProvider.GetRequiredService<ISagaStateStore>());
        Assert.IsType<InMemorySagaStateStore>(first.ServiceProvider.GetRequiredService<ISagaStateStore>());
        Assert.NotSame(first.ServiceProvider.GetRequiredService<SagaCoordinator>(), second.ServiceProvider.GetRequiredService<SagaCoordinator>());
        Assert.Same(first.ServiceProvider.GetRequiredService<SagaCoordinator>(), first.ServiceProvider.GetRequiredService<ISagaFlow>());
        Assert.NotNull(first.ServiceProvider.GetRequiredService<SagaInstanceManager>());
        Assert.NotNull(first.ServiceProvider.GetRequiredService<SagaStepExecutor>());
        Assert.NotNull(first.ServiceProvider.GetRequiredService<TransitionResolver>());
        Assert.NotNull(first.ServiceProvider.GetRequiredService<ProcessStateManager>());
        Assert.NotNull(first.ServiceProvider.GetRequiredService<CommandDispatcher>());
        Assert.NotNull(first.ServiceProvider.GetRequiredService<OutcomeEventReceiver>());
        Assert.NotNull(first.ServiceProvider.GetRequiredService<SagaTimeoutDetector>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ZeroLocalDefinitionsIncludingRemoteOnlyRegistryFailClearly(bool registerRemote)
    {
        await using var host = await RuntimeHost.Start(loadLocal: false);
        if (registerRemote) host.Registry.RegisterRemote(new DefinitionYamlCodec().Parse(Yaml), "https://remote");
        await using var scope = host.App.Services.CreateAsyncScope();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<ISagaFlow>().StartAsync(new { id = 1 }));
        Assert.Contains("No local", error.Message);
        Assert.Empty(host.Bus.Commands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultipleLocalProcessesOrVersionsAreAmbiguousRatherThanSelectingLatest(bool secondVersion)
    {
        await using var host = await RuntimeHost.Start();
        host.Registry.RegisterLocalBatch([new DefinitionYamlCodec().Parse(secondVersion ? Yaml.Replace("\"1.0\"", "\"2.0\"") : Yaml.Replace("purchase", "another"))]);
        await using var scope = host.App.Services.CreateAsyncScope();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<ISagaFlow>().StartAsync(new { id = 1 }));
        Assert.Contains("ambiguous", error.Message);
        Assert.Empty(host.Bus.Commands);
    }

    [Fact]
    public async Task RemoteDefinitionDoesNotParticipateInLocalProcessSelection()
    {
        await using var host = await RuntimeHost.Start();
        host.Registry.RegisterRemote(new DefinitionYamlCodec().Parse(Yaml.Replace("purchase", "remote-process")), "https://remote");
        await using var scope = host.App.Services.CreateAsyncScope();
        var id = await scope.ServiceProvider.GetRequiredService<ISagaFlow>().StartAsync("payload");
        var state = (await scope.ServiceProvider.GetRequiredService<ISagaStateStore>().FindAsync(id))!;
        Assert.Equal("purchase", state.DefinitionName);
        Assert.Equal(2, host.Registry.Snapshot().Count);
        Assert.Single(host.Bus.Commands);
    }

    [Fact]
    public async Task HostCanLoadYamlWithoutTransportButStartFailsExplicitly()
    {
        await using var host = await RuntimeHost.Start(registerTransport: false);
        await using var scope = host.App.Services.CreateAsyncScope();
        Assert.Single(host.Registry.Snapshot());
        Assert.Null(scope.ServiceProvider.GetService<ISagaMessageBus>());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<ISagaFlow>().StartAsync("payload"));
        Assert.Contains("No ISagaMessageBus transport adapter", error.Message);
        Assert.Empty(host.Bus.Commands);
    }

    [Fact]
    public async Task DashboardHostingStillServesItsAssetsWithoutAMessageTransport()
    {
        await using var host = await RuntimeHost.Start(registerTransport: false);
        var address = host.App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        var response = await client.GetAsync("/sagaflow/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("text/html", response.Content.Headers.ContentType!.ToString());
    }

    [Fact]
    public async Task DashboardApiReturnsRealListAndCompleteDetailWithoutMutatingState()
    {
        await using var host = await RuntimeHost.Start();
        await using var scope = host.App.Services.CreateAsyncScope();
        var id = await scope.ServiceProvider.GetRequiredService<ISagaFlow>().StartAsync(new { orderId = 42 });
        var store = scope.ServiceProvider.GetRequiredService<ISagaStateStore>();
        var before = (await store.FindAsync(id))!;
        var address = host.App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };

        using var list = JsonDocument.Parse(await client.GetStringAsync("/sagaflow/api/instances"));
        var summary = Assert.Single(list.RootElement.EnumerateArray());
        Assert.Equal(id.ToString("N"), summary.GetProperty("id").GetString());
        Assert.Equal("purchase", summary.GetProperty("processName").GetString());
        Assert.Equal("Running", summary.GetProperty("status").GetString());

        using var detail = JsonDocument.Parse(await client.GetStringAsync($"/sagaflow/api/instances/{id:N}"));
        Assert.Equal(2, detail.RootElement.GetProperty("steps").GetArrayLength());
        Assert.Equal("Dispatched", detail.RootElement.GetProperty("steps")[0].GetProperty("status").GetString());
        Assert.Equal("Pending", detail.RootElement.GetProperty("steps")[1].GetProperty("status").GetString());
        Assert.Empty(detail.RootElement.GetProperty("compensations").EnumerateArray());
        Assert.Equal("order", detail.RootElement.GetProperty("definitionDomain").GetString());
        var starter = detail.RootElement.GetProperty("domainStarter");
        Assert.Equal("Local operation", starter.GetProperty("operationName").GetString());
        Assert.Equal("Local rollback", starter.GetProperty("rollbackOperationName").GetString());
        foreach (var property in new[] { "status", "startedAt", "completedAt", "input", "result" })
            Assert.Equal(JsonValueKind.Null, starter.GetProperty(property).ValueKind);
        Assert.Equal(before.Revision, (await store.FindAsync(id))!.Revision);
    }

    [Theory]
    [InlineData("/sagaflow")]
    [InlineData("/operations")]
    public async Task DashboardDefinitionsMatchActualOrderYamlAndFrontendFixtureWithoutDiscovery(string dashboardPath)
    {
        var yaml = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "workflow-order-process.yaml"));
        await using var host = await RuntimeHost.Start(registerTransport: false, yaml: yaml, dashboardPath: dashboardPath);
        host.Registry.RegisterRemote(new DefinitionYamlCodec().Parse(Yaml), "https://remote");
        var before = host.Registry.Snapshot();
        var address = host.App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        using var definitions = JsonDocument.Parse(await client.GetStringAsync($"{dashboardPath}/api/definitions"));
        var definition = Assert.Single(definitions.RootElement.EnumerateArray());
        Assert.Equal("order-process", definition.GetProperty("name").GetString());
        Assert.Equal("order", definition.GetProperty("domain").GetString());
        Assert.Equal("1.0", definition.GetProperty("version").GetString());
        var steps = definition.GetProperty("steps");
        Assert.Equal(new[] { "payment", "inventory", "managerNotification", "managerApproval", "shipment", "notification" },
            steps.EnumerateArray().Select(step => step.GetProperty("name").GetString()));
        Assert.Equal("RefundPayment", steps[0].GetProperty("compensation").GetProperty("command").GetString());
        var condition = steps[1].GetProperty("transitions")[0].GetProperty("condition");
        Assert.Equal("context.totalQuantity", condition.GetProperty("path").GetString());
        Assert.Equal("greaterThan", condition.GetProperty("operator").GetString());
        Assert.Equal("1000", condition.GetProperty("valueJson").GetString());
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "workflow-order-definition.json")));
        // The frontend graph tests use this API snapshot, not a second YAML parser or a hand-written process.
        Assert.True(JsonElement.DeepEquals(fixture.RootElement, definitions.RootElement), "Regenerate the frontend fixture from the Dashboard definitions API if Order YAML changes.");
        Assert.Equal(before, host.Registry.Snapshot());
        Assert.Empty(await host.App.Services.GetRequiredService<ISagaStateStore>().ListAsync());
        using var post = await client.PostAsync($"{dashboardPath}/api/definitions", null);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
    }

    [Fact]
    public async Task DashboardDefinitionsPreserveExactComparisonLiteralsAndDeclaredReferences()
    {
        const string yaml = """
            name: return-flow
            domain: returns
            version: "2.0"
            steps:
              - name: inspect
                command: InspectReturn
                transitions:
                  - event: success
                    when:
                      path: context.quantity
                      operator: greaterThan
                      value: 9007199254740993
                    nextStep: completed
                  - event: success
                    nextStep: compensation
                  - event: failure
                    nextStep: compensation
            bigPicture:
              - domain: accounts
                process: reconciliation
                endpoint: https://accounts.example
            """;
        await using var host = await RuntimeHost.Start(registerTransport: false, yaml: yaml);
        var address = host.App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        using var definitions = JsonDocument.Parse(await client.GetStringAsync("/sagaflow/api/definitions"));
        var definition = Assert.Single(definitions.RootElement.EnumerateArray());
        var step = definition.GetProperty("steps")[0];
        Assert.Equal("9007199254740993", step.GetProperty("transitions")[0].GetProperty("condition").GetProperty("valueJson").GetString());
        Assert.Equal("9007199254740993", step.GetProperty("transitions")[0].GetProperty("condition").GetProperty("value").GetRawText());
        Assert.Equal(JsonValueKind.Null, step.GetProperty("timeoutLimit").ValueKind);
        Assert.Equal(1, step.GetProperty("maxAttempts").GetInt32());
        Assert.Equal("reconciliation", Assert.Single(definition.GetProperty("bigPicture").EnumerateArray()).GetProperty("process").GetString());
    }

    [Fact]
    public async Task DisabledDashboardDoesNotExposeDefinitions()
    {
        await using var host = await RuntimeHost.Start(registerTransport: false, configureDashboard: options => options.Enabled = false);
        var address = host.App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        using var response = await client.GetAsync("/sagaflow/api/definitions");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ExistingDashboardInstanceApiDoesNotRequireADefinitionRegistry()
    {
        using var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = "/sagaflow/api/instances";
        context.Response.Body = new MemoryStream();
        var middleware = new SagaFlowDashboardMiddleware(_ => Task.CompletedTask, new SagaFlowDashboardOptions());
        await middleware.InvokeAsync(context, new InMemorySagaStateStore());
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var instances = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Empty(instances.RootElement.EnumerateArray());
    }

    [Theory]
    [InlineData("order", "CreatePurchase", "CancelPurchase")]
    [InlineData("returns", "RegisterReturn", "CancelReturn")]
    public async Task DashboardUsesApplicationStarterPresentationWithoutAddingSagaSteps(string domain, string operation, string rollback)
    {
        await using var host = await RuntimeHost.Start(yaml: Yaml.Replace("domain: order", $"domain: {domain}"),
            configureDashboard: options => options.DescribeDomainStarter = instance => new SagaDomainStarterDisplay
            {
                OperationName = operation, RollbackOperationName = rollback,
                Status = "Completed", Result = instance.Context.Clone()
            });
        await using var scope = host.App.Services.CreateAsyncScope();
        var id = await scope.ServiceProvider.GetRequiredService<ISagaFlow>().StartAsync(new { reference = 42 });
        var store = scope.ServiceProvider.GetRequiredService<ISagaStateStore>();
        var before = (await store.FindAsync(id))!;
        var address = host.App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };

        using var detail = JsonDocument.Parse(await client.GetStringAsync($"/sagaflow/api/instances/{id:N}"));
        Assert.Equal(domain, detail.RootElement.GetProperty("definitionDomain").GetString());
        var starter = detail.RootElement.GetProperty("domainStarter");
        Assert.Equal(operation, starter.GetProperty("operationName").GetString());
        Assert.Equal(rollback, starter.GetProperty("rollbackOperationName").GetString());
        Assert.Equal("Completed", starter.GetProperty("status").GetString());
        Assert.Equal(42, starter.GetProperty("result").GetProperty("reference").GetInt32());
        Assert.Equal(JsonValueKind.Null, starter.GetProperty("startedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, starter.GetProperty("completedAt").ValueKind);
        Assert.Equal(new[] { "pay", "ship" }, detail.RootElement.GetProperty("steps").EnumerateArray().Select(step => step.GetProperty("stepName").GetString()));
        Assert.Equal(before.Revision, (await store.FindAsync(id))!.Revision);
    }

    [Fact]
    public async Task SignalRPublishesChangedInstanceIdAtConfiguredDashboardPath()
    {
        await using var host = await RuntimeHost.Start(dashboardPath: "/operations");
        var address = host.App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        await using var connection = new HubConnectionBuilder()
            .WithUrl($"{address}/operations/hub")
            .WithAutomaticReconnect()
            .Build();
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<string>("SagaInstanceChanged", instanceId => received.TrySetResult(instanceId));
        await connection.StartAsync();

        await using var scope = host.App.Services.CreateAsyncScope();
        var id = await scope.ServiceProvider.GetRequiredService<ISagaFlow>().StartAsync(new { orderId = 73 });

        Assert.Equal(id.ToString("N"), await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task HostedDetectionMakesTimeoutVisibleThroughDashboardApi()
    {
        var timeoutYaml = Yaml.Replace("command: AuthorizePayment", "command: AuthorizePayment\n    timeoutLimit: 1ms");
        await using var host = await RuntimeHost.Start(yaml: timeoutYaml);
        await using var scope = host.App.Services.CreateAsyncScope();
        var id = await scope.ServiceProvider.GetRequiredService<ISagaFlow>().StartAsync(new { orderId = 84 });
        var store = scope.ServiceProvider.GetRequiredService<ISagaStateStore>();

        SagaInstance? state = null;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            state = await store.FindAsync(id);
            if (state?.Status == SagaStatus.TimedOut) break;
            await Task.Delay(50);
        }
        Assert.NotNull(state);
        Assert.Equal(SagaStatus.TimedOut, state.Status);

        var address = host.App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        using var list = JsonDocument.Parse(await client.GetStringAsync("/sagaflow/api/instances"));
        Assert.Equal("TimedOut", Assert.Single(list.RootElement.EnumerateArray()).GetProperty("status").GetString());
        using var detail = JsonDocument.Parse(await client.GetStringAsync($"/sagaflow/api/instances/{id:N}"));
        var step = detail.RootElement.GetProperty("steps")[0];
        Assert.Equal("TimedOut", step.GetProperty("status").GetString());
        Assert.Equal("00:00:00.0010000", step.GetProperty("timeoutLimit").GetString());
        Assert.Equal(JsonValueKind.String, step.GetProperty("timedOutAt").ValueKind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoreAdapterCanBeReplacedBeforeOrAfterSagaFlowRegistration(bool registerFirst)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Host.UseDefaultServiceProvider(options => { options.ValidateScopes = true; options.ValidateOnBuild = true; });
        var services = builder.Services;
        var replacement = new TrackingStore();
        if (registerFirst) services.AddSingleton<ISagaStateStore>(replacement);
        services.AddSagaFlow();
        if (!registerFirst) services.AddSingleton<ISagaStateStore>(replacement);
        services.AddScoped<ISagaMessageBus, ScopedTransport>();
        await using var app = builder.Build();
        var provider = app.Services;
        provider.GetRequiredService<ProcessDefinitionRegistry>().RegisterLocalBatch([new DefinitionYamlCodec().Parse(Yaml)]);
        using var scope = provider.CreateScope();
        var id = await scope.ServiceProvider.GetRequiredService<ISagaFlow>().StartAsync(new { id = 1 });
        Assert.Same(replacement, scope.ServiceProvider.GetRequiredService<ISagaStateStore>());
        Assert.Equal(1, replacement.Saves);
        Assert.Equal(1, replacement.Updates);
        Assert.NotNull(await ((ISagaStateStore)replacement).FindAsync(id));
        Assert.Single(Assert.IsType<ScopedTransport>(scope.ServiceProvider.GetRequiredService<ISagaMessageBus>()).Commands);
    }

    private const string Yaml = """
        name: purchase
        version: "1.0"
        domain: order
        steps:
          - name: pay
            command: AuthorizePayment
            compensation:
              command: RefundPayment
            transitions:
              - event: success
                nextStep: ship
              - event: failure
                nextStep: compensation
          - name: ship
            command: CreateShipment
            transitions:
              - event: success
                nextStep: completed
              - event: failure
                nextStep: compensation
        """;

    private sealed class StarterBusinessService(ISagaFlow sagaFlow)
    {
        public bool LocalWorkCompleted { get; private set; }
        public Task<Guid> SaveThenStartAsync<T>(T payload)
        { LocalWorkCompleted = true; return sagaFlow.StartAsync(payload); }
    }

    // Test transport only; production has no default message-bus adapter.
    private sealed class TestTransport : ISagaMessageBus
    {
        public ConcurrentQueue<SagaCommand> Commands { get; } = new();
        public ValueTask PublishAsync(SagaCommand command, CancellationToken cancellationToken = default)
        { Commands.Enqueue(command); return ValueTask.CompletedTask; }
    }

    private sealed class ScopedTransport : ISagaMessageBus
    {
        public List<SagaCommand> Commands { get; } = [];
        public ValueTask PublishAsync(SagaCommand command, CancellationToken cancellationToken = default)
        { Commands.Add(command); return ValueTask.CompletedTask; }
    }

    private sealed class TrackingStore : ISagaStateStore
    {
        private readonly InMemorySagaStateStore _inner = new();
        public int Saves { get; private set; }
        public int Updates { get; private set; }
        public ValueTask SaveAsync(SagaInstance instance, CancellationToken cancellationToken = default)
        { Saves++; return _inner.SaveAsync(instance, cancellationToken); }
        public ValueTask<SagaInstance?> FindAsync(string instanceId, CancellationToken cancellationToken = default) => _inner.FindAsync(instanceId, cancellationToken);
        public ValueTask<IReadOnlyList<SagaInstance>> ListAsync(CancellationToken cancellationToken = default) => _inner.ListAsync(cancellationToken);
        public ValueTask<TResult> UpdateAsync<TResult>(string instanceId, Func<SagaInstance, TResult> update, CancellationToken cancellationToken = default)
        { Updates++; return _inner.UpdateAsync(instanceId, update, cancellationToken); }
    }

    private sealed class RuntimeHost(WebApplication app, TestTransport bus, string directory) : IAsyncDisposable
    {
        public WebApplication App { get; } = app;
        public TestTransport Bus { get; } = bus;
        public ProcessDefinitionRegistry Registry => App.Services.GetRequiredService<ProcessDefinitionRegistry>();
        public static async Task<RuntimeHost> Start(bool loadLocal = true, bool registerTransport = true, string? yaml = null,
            string dashboardPath = "/sagaflow", Action<SagaFlowDashboardOptions>? configureDashboard = null)
        {
            var directory = Directory.CreateTempSubdirectory("sagaflow-runtime-").FullName;
            if (loadLocal) await File.WriteAllTextAsync(Path.Combine(directory, "purchase.yaml"), yaml ?? Yaml);
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Host.UseDefaultServiceProvider(options => { options.ValidateScopes = true; options.ValidateOnBuild = true; });
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var bus = new TestTransport();
            if (registerTransport) builder.Services.AddSingleton<ISagaMessageBus>(bus);
            builder.Services.AddSagaFlow(options =>
            {
                options.LoadFromDirectory(directory);
                options.Dashboard.Path = dashboardPath;
                configureDashboard?.Invoke(options.Dashboard);
            });
            builder.Services.AddScoped<StarterBusinessService>();
            var app = builder.Build();
            try { await app.StartAsync(); return new(app, bus, directory); }
            catch { await app.DisposeAsync(); Directory.Delete(directory, true); throw; }
        }
        public async ValueTask DisposeAsync()
        { await App.DisposeAsync(); Directory.Delete(directory, true); }
    }
}
