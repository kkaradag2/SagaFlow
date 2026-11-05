using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Messages;
using SagaFlow.Core.State;
using SagaFlow.Engine.Persistence.InMemory;
using SagaFlow.Hosting.DependencyInjection;
using SagaFlow.Hosting.Endpoints;

namespace SagaFlow.Hosting.Tests;

public sealed class InstanceRelationshipApiTests
{
    // Fail the test if an instance-scoped request ever scans the global store.
    private sealed class LookupOnlyStore : ISagaStateStore
    {
        private readonly InMemorySagaStateStore inner = new();
        public ValueTask SaveAsync(SagaInstance value, CancellationToken ct = default) => inner.SaveAsync(value, ct);
        public ValueTask<SagaInstance?> FindAsync(string id, CancellationToken ct = default) => inner.FindAsync(id, ct);
        public ValueTask<IReadOnlyList<SagaInstance>> ListAsync(CancellationToken ct = default) => throw new InvalidOperationException("Unbounded scan");
        public ValueTask<T> UpdateAsync<T>(string id, Func<SagaInstance, T> update, CancellationToken ct = default) => inner.UpdateAsync(id, update, ct);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HttpQueryIsLocalBoundedFilteredAndDoesNotLeakBusinessOrTransportData(bool automaticMiddleware)
    {
        var store = new LookupOnlyStore();
        var owner = new ProcessIdentity("order", "order-process", "1.0");
        var remote = new ProcessIdentity("return", "return-process", "2.0");
        var id = Guid.NewGuid().ToString("N");
        var requests = Enumerable.Range(0, 3).Select(i => new ProcessInvocationRequest(
            new(Guid.NewGuid().ToString("N"), "local-correlation", owner, id, "step" + i, remote),
            JsonSerializer.SerializeToElement(new { secret = "payload-secret" }))).ToArray();
        var now = DateTimeOffset.UtcNow;
        await store.SaveAsync(new() { Id = id, DefinitionDomain = owner.Domain, DefinitionName = owner.Process, DefinitionVersion = owner.Version,
            CorrelationId = "local-correlation", Status = SagaStatus.Running, Context = JsonSerializer.SerializeToElement("context-secret"),
            Steps = requests.Select((r,i) => new SagaStepState { StepName = "step" + i, Invocation = r,
                InvocationProgress = new(ExternalProcessStatus.Prepared, null, now, now, null),
                Publication = new("attempt-secret", now, "transport-secret") }).ToArray() });
        var plain = new SagaInstance { DefinitionDomain = owner.Domain, DefinitionName = owner.Process, DefinitionVersion = owner.Version };
        await store.SaveAsync(plain);
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSagaFlow(o => o.Dashboard.Path = automaticMiddleware ? "/sagaflow" : "/embedded");
        builder.Services.AddSingleton<ISagaStateStore>(store);
        await using var app = builder.Build(); app.MapSagaFlowDashboard(); await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        var url = $"/sagaflow/api/instances/{id}/relationships";
        async Task<JsonElement> Get(string address)
        { using var json = JsonDocument.Parse(await client.GetStringAsync(address)); return json.RootElement.Clone(); }
        var first = await Get(url + "?limit=2&domain=order&process=order-process&version=1.0&perspective=Source");
        Assert.Equal(2, first.GetProperty("items").GetArrayLength()); Assert.Equal(2, first.GetProperty("nextOffset").GetInt32());
        var last = await Get(url + "?limit=2&offset=2");
        Assert.Equal(JsonValueKind.Null, last.GetProperty("nextOffset").ValueKind);
        var all = first.GetProperty("items").EnumerateArray().Concat(last.GetProperty("items").EnumerateArray()).ToArray();
        Assert.Equal(requests.Select(r => r.Identity.InvocationId), all.Select(r => r.GetProperty("invocationId").GetString()));
        Assert.All(all, r => Assert.Equal(JsonValueKind.Null, r.GetProperty("targetSagaInstanceId").ValueKind));
        foreach (var query in new[] { "?domain=return", "?process=other", "?version=2.0" })
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(url + query)).StatusCode);
        foreach (var query in new[] { "?limit=0", "?limit=101", "?offset=-1", "?perspective=Remote", "?limit=abc" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(url + query)).StatusCode);
        Assert.Empty((await Get(url + "?perspective=Target")).GetProperty("items").EnumerateArray());
        Assert.Empty((await Get(url + "?offset=100")).GetProperty("items").EnumerateArray());
        Assert.Empty((await Get($"/sagaflow/api/instances/{plain.Id}/relationships")).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/sagaflow/api/instances/missing/relationships")).StatusCode);
        var before = (await Get(url)).GetRawText();
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PostAsync(url, null)).StatusCode);
        if (!automaticMiddleware)
            Assert.Equal(before, (await Get($"/embedded/api/instances/{id}/relationships")).GetRawText());
        await store.UpdateAsync(id, s => { s.Steps[0].Invocation = requests[0]; return true; });
        Assert.Equal(before, (await Get(url)).GetRawText());
        foreach (var secret in new[] { "payload-secret", "context-secret", "attempt-secret", "transport-secret", "publication", "observedMessages" })
            Assert.DoesNotContain(secret, before);
        await store.UpdateAsync(id, s => { s.Steps[0].Invocation = new(new(requests[0].Identity.InvocationId,
            "local-correlation", remote, id, "step0", remote), requests[0].Payload); return true; });
        Assert.Equal(2, (await Get(url)).GetProperty("items").GetArrayLength());
    }
}
