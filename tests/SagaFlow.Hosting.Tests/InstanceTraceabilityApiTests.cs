using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Definitions;
using SagaFlow.Core.Messages;
using SagaFlow.Core.State;
using SagaFlow.Engine.Coordination;
using SagaFlow.Engine.Messaging;
using SagaFlow.Engine.ProcessDefinition;
using SagaFlow.Hosting.DependencyInjection;
using SagaFlow.Hosting.Endpoints;

namespace SagaFlow.Hosting.Tests;

public sealed class InstanceTraceabilityApiTests
{
    private sealed class Probe
    {
        public TaskCompletionSource<string> Prepared { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Handler(ISagaFlow flow, Probe probe) : ILocalSagaStepHandler
    {
        public async Task ExecuteAsync(SagaCommand command, CancellationToken ct)
        {
            await flow.InvokeAsync("remote-process", command.Payload, ct);
            probe.Prepared.TrySetResult(command.Headers.SagaInstanceId);
            await probe.Release.Task.WaitAsync(ct);
        }
    }
    private sealed class Bus : ISagaMessageBus, IProcessInvocationBus
    {
        public ProcessInvocationRequest? Request { get; private set; }
        public ValueTask PublishAsync(SagaCommand value, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask PublishAsync(ProcessInvocationRequest value, CancellationToken ct = default) { Request = value; return ValueTask.CompletedTask; }
        public ValueTask PublishAsync(ProcessInvocationAccepted value, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask PublishAsync(ProcessInvocationRejected value, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask PublishAsync(ProcessInvocationTerminalOutcome value, CancellationToken ct = default) => ValueTask.CompletedTask;
    }
    private static SagaDefinition Definition(bool source) => new()
    {
        Domain = source ? "source" : "target", Name = source ? "source-process" : "remote-process", Version = source ? "1.0" : "2.0",
        Steps = [new() { Name = "work", Command = "Work", External = source,
            Transitions = [new() { Event = "success", NextStep = "completed" }, new() { Event = "failure", NextStep = "compensation" }] }],
        BigPicture = source ? [new() { Domain = "target", Process = "remote-process", Version = "2.0", Endpoint = "https://private-discovery:7001", Relationship = "invokes" }] : []
    };
    private static async Task<WebApplication> Host(bool source)
    {
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSagaFlow(); builder.Services.AddSingleton<Bus>(); builder.Services.AddSingleton<Probe>();
        builder.Services.AddSingleton<ISagaMessageBus>(p => p.GetRequiredService<Bus>());
        builder.Services.AddSingleton<IProcessInvocationBus>(p => p.GetRequiredService<Bus>());
        builder.Services.AddScoped<ProcessInvocationPublisher>();
        if (source) builder.Services.AddScoped<ILocalSagaStepHandler, Handler>();
        var app = builder.Build(); app.Services.GetRequiredService<ProcessDefinitionRegistry>().RegisterLocalBatch([Definition(source)]);
        app.MapSagaFlowDashboard(); await app.StartAsync(); return app;
    }
    private static HttpClient Client(WebApplication app) => new() { BaseAddress = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
    private static async Task<JsonElement> Detail(HttpClient client, string id)
    {
        using var doc = await JsonDocument.ParseAsync(await client.GetStreamAsync("/sagaflow/api/instances/" + id));
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task ActualEngineStateProjectsBothOriginsAndSignalRTracksLocalObservations()
    {
        await using var source = await Host(true); await using var target = await Host(false);
        using var sourceClient = Client(source); using var targetClient = Client(target);
        await using var hub = new HubConnectionBuilder().WithUrl(new Uri(sourceClient.BaseAddress!, "/sagaflow/hub")).Build();
        var changes = new ConcurrentQueue<string>(); hub.On<string>("SagaInstanceChanged", id => changes.Enqueue(id)); await hub.StartAsync();
        await using var scope = source.Services.CreateAsyncScope();
        var start = scope.ServiceProvider.GetRequiredService<ISagaFlow>().StartAsync(new { value = 42 });
        var probe = source.Services.GetRequiredService<Probe>();
        var id = await probe.Prepared.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var prepared = await Detail(sourceClient, id);
            Assert.Equal("Local", prepared.GetProperty("origin").GetString());
            var view = prepared.GetProperty("steps")[0].GetProperty("invocation");
            Assert.True(prepared.GetProperty("steps")[0].GetProperty("external").GetBoolean());
            Assert.Equal("Prepared", view.GetProperty("lastKnownStatus").GetString());
            Assert.Equal(JsonValueKind.Null, view.GetProperty("targetSagaInstanceId").ValueKind);
            Assert.Equal(JsonValueKind.Null, view.GetProperty("lastObservedAt").ValueKind);
            probe.Release.TrySetResult(); await start;
            var requested = await Detail(sourceClient, id);
            Assert.Equal("Requested", requested.GetProperty("steps")[0].GetProperty("invocation").GetProperty("lastKnownStatus").GetString());
            var request = source.Services.GetRequiredService<Bus>().Request!;
            await using var targetScope = target.Services.CreateAsyncScope();
            var targetState = await targetScope.ServiceProvider.GetRequiredService<ProcessInvocationManager>().GetOrCreateTargetAsync(request);
            var incoming = await Detail(targetClient, targetState.Id);
            Assert.Equal("External", incoming.GetProperty("origin").GetString());
            var origin = incoming.GetProperty("incomingInvocation");
            Assert.Equal(id, origin.GetProperty("sourceSagaInstanceId").GetString());
            Assert.Equal(request.Identity.CorrelationId, origin.GetProperty("sourceCorrelationId").GetString());
            Assert.NotEqual(origin.GetProperty("sourceCorrelationId").GetString(), incoming.GetProperty("correlationId").GetString());
            Assert.Equal(request.Message.OccurredAt, origin.GetProperty("sourceRequestCreatedAt").GetDateTimeOffset());
            Assert.Equal(targetState.IncomingInvocation!.AcceptedAt, origin.GetProperty("acceptedAt").GetDateTimeOffset());
            var manager = scope.ServiceProvider.GetRequiredService<ProcessInvocationManager>();
            foreach (var status in new[] { ExternalProcessStatus.Accepted, ExternalProcessStatus.Running, ExternalProcessStatus.Completed })
            {
                var before = changes.Count;
                await manager.ObserveAsync(request.Identity, status, targetState.Id);
                await Wait(() => changes.Count > before);
                var current = await Detail(sourceClient, id);
                var invocation = current.GetProperty("steps")[0].GetProperty("invocation");
                Assert.Equal(status.ToString(), invocation.GetProperty("lastKnownStatus").GetString());
                Assert.Equal(JsonValueKind.String, invocation.GetProperty("lastObservedAt").ValueKind);
                Assert.Equal("Running", current.GetProperty("status").GetString());
            }
            var beforeDuplicate = await Detail(sourceClient, id);
            await manager.ObserveAsync(request.Identity, ExternalProcessStatus.Completed, targetState.Id);
            var duplicate = await Detail(sourceClient, id);
            Assert.Equal(beforeDuplicate.GetProperty("steps")[0].GetProperty("invocation").GetProperty("updatedAt").GetString(), duplicate.GetProperty("steps")[0].GetProperty("invocation").GetProperty("updatedAt").GetString());
            await source.Services.GetRequiredService<ISagaStateStore>().UpdateAsync(id, state =>
            {
                state.Status = SagaStatus.Failed; state.Steps[0].Status = SagaStepStatus.Failed;
                state.Steps[0].Reconciliation = new("Local forward publication failed", DateTimeOffset.UtcNow);
                state.Steps[0].Publication = state.Steps[0].Publication! with { LastError = "private-transport-detail" }; return true;
            });
            var failed = await Detail(sourceClient, id);
            Assert.Equal("Failed", failed.GetProperty("status").GetString());
            Assert.Equal("Completed", failed.GetProperty("steps")[0].GetProperty("invocation").GetProperty("lastKnownStatus").GetString());
            Assert.Equal("Local forward publication failed", failed.GetProperty("steps")[0].GetProperty("invocation").GetProperty("reconciliation").GetProperty("reason").GetString());
            var json = failed.GetRawText() + incoming.GetRawText();
            Assert.DoesNotContain("private-transport-detail", json); Assert.DoesNotContain("private-discovery", json);
            Assert.DoesNotContain("observedMessages", json); Assert.DoesNotContain("activeAttemptId", json);
            using var sourceList = JsonDocument.Parse(await sourceClient.GetStringAsync("/sagaflow/api/instances"));
            using var targetList = JsonDocument.Parse(await targetClient.GetStringAsync("/sagaflow/api/instances"));
            Assert.Equal(id, Assert.Single(sourceList.RootElement.EnumerateArray()).GetProperty("id").GetString());
            Assert.Equal(targetState.Id, Assert.Single(targetList.RootElement.EnumerateArray()).GetProperty("id").GetString());
            Assert.All(changes, changed => Assert.Equal(id, changed));
        }
        finally { probe.Release.TrySetResult(); }
    }
    private static async Task Wait(Func<bool> predicate)
    {
        var limit = DateTime.UtcNow.AddSeconds(10);
        while (!predicate() && DateTime.UtcNow < limit) await Task.Delay(20);
        Assert.True(predicate(), "SignalR state-change notification was not received.");
    }
}
