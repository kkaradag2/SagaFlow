using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
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
using SagaFlow.Hosting.DependencyInjection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

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
        Assert.Equal(before.Revision, (await store.FindAsync(id))!.Revision);
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
        public static async Task<RuntimeHost> Start(bool loadLocal = true, bool registerTransport = true)
        {
            var directory = Directory.CreateTempSubdirectory("sagaflow-runtime-").FullName;
            if (loadLocal) await File.WriteAllTextAsync(Path.Combine(directory, "purchase.yaml"), Yaml);
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Host.UseDefaultServiceProvider(options => { options.ValidateScopes = true; options.ValidateOnBuild = true; });
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var bus = new TestTransport();
            if (registerTransport) builder.Services.AddSingleton<ISagaMessageBus>(bus);
            builder.Services.AddSagaFlow(options => options.LoadFromDirectory(directory));
            builder.Services.AddScoped<StarterBusinessService>();
            var app = builder.Build();
            try { await app.StartAsync(); return new(app, bus, directory); }
            catch { await app.DisposeAsync(); Directory.Delete(directory, true); throw; }
        }
        public async ValueTask DisposeAsync()
        { await App.DisposeAsync(); Directory.Delete(directory, true); }
    }
}
