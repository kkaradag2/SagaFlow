using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Definitions;
using SagaFlow.Core.Messages;
using SagaFlow.Core.State;
using SagaFlow.Engine.Coordination;
using SagaFlow.Engine.Messaging;
using SagaFlow.Engine.Notifications;
using SagaFlow.Engine.ProcessDefinition;
using SagaFlow.Hosting.DependencyInjection;
using SagaFlow.Storage.InMemory.DependencyInjection;
using SagaFlow.Hosting.Endpoints;
using SagaFlow.Messaging.RabbitMQ.DependencyInjection;

namespace SagaFlow.Messaging.RabbitMQ.Tests;

public sealed partial class ProcessInvocationIntegrationTests
{
    private sealed record AcceptanceHost(WebApplication App, string Url, string DiscoveryUrl) : IAsyncDisposable
    {
        public HttpClient Client() => new() { BaseAddress = new(Url) };
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    private async Task<AcceptanceHost> StartAcceptanceHost(SagaDefinition[] definitions, string? invoke = null,
        MetadataProbe? pause = null, bool rabbit = true)
    {
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
        ListenOptions? http = null, grpc = null;
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Listen(IPAddress.Loopback, 0, endpoint => { endpoint.Protocols = HttpProtocols.Http1; http = endpoint; });
            server.Listen(IPAddress.Loopback, 0, endpoint => { endpoint.Protocols = HttpProtocols.Http2; grpc = endpoint; });
        });
        builder.Services.AddSagaFlow(options =>
        {
            options.Storage.UseInMemory();
            options.UseDashboard();
            options.Publication.Enabled = true;
            options.Discovery.RequestTimeout = TimeSpan.FromSeconds(1);
        });
        builder.Services.AddScoped<IProcessInvocationPolicy, Policy>();
        if (invoke is not null)
        {
            builder.Services.AddSingleton(new Target(invoke, false)); builder.Services.AddScoped<ILocalSagaStepHandler, Handler>();
        }
        if (pause is not null)
        { builder.Services.AddSingleton(pause); builder.Services.AddScoped<ILocalSagaStepHandler, PauseMetadata>(); }
        if (rabbit) builder.Services.AddSagaFlowRabbitMq(options =>
        {
            options.HostName = broker.HostName; options.Port = broker.Port; options.UserName = broker.UserName;
            options.Password = broker.Password; options.VirtualHost = broker.VirtualHost; options.EnableProcessInvocations = true;
        });
        var app = builder.Build();
        app.Services.GetRequiredService<ProcessDefinitionRegistry>().RegisterLocalBatch(definitions);
        app.MapSagaFlowDefinitionDiscovery();
        try { await app.StartAsync(); }
        catch { await app.DisposeAsync(); throw; }
        return new(app, $"http://127.0.0.1:{http!.IPEndPoint!.Port}/", $"http://127.0.0.1:{grpc!.IPEndPoint!.Port}/");
    }
    private static async Task<string> StartAcceptanceSaga(AcceptanceHost host, int reference = 1)
    {
        await using var scope = host.App.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<ISagaFlow>().StartAsync(new { allow = true, reference })).ToString("N");
    }
    private static async Task<JsonElement> RelationshipPage(HttpClient client, string id, string query = "")
    {
        using var json = JsonDocument.Parse(await client.GetStringAsync($"sagaflow/api/instances/{id}/relationships{query}"));
        return json.RootElement.Clone();
    }

    [RabbitMqFact]
    public async Task BigPictureActualBrokerHttpAndOptionalChromeAcceptance()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var target = Definition("target" + suffix, "remote", "targetStep" + suffix);
        var catalog = Definition("catalog" + suffix, "catalog", "catalogStep" + suffix);
        await using var targetHost = await StartAcceptanceHost([target]);
        await using var catalogHost = await StartAcceptanceHost([catalog], rabbit: false);
        var source = new SagaDefinition
        {
            Domain = "source" + suffix, Name = "local", Version = "1.0",
            BigPicture = [
                new() { Domain = target.Domain, Process = target.Name, Version = "1.0", Endpoint = targetHost.DiscoveryUrl.TrimEnd('/'), Relationship = "invokes" },
                new() { Domain = catalog.Domain, Process = catalog.Name, Version = "1.0", Endpoint = catalogHost.DiscoveryUrl.TrimEnd('/'), Relationship = "related" },
                new() { Domain = "offline", Process = "unknown-process", Version = "1.0", Endpoint = "http://127.0.0.1:1", Relationship = "invokes" }],
            Steps = Enumerable.Range(0, 2).Select(i => new SagaStepDefinition { Name = "invoke" + i, Command = "InvokeRemote", External = true,
                Transitions = [new() { Event = "success", NextStep = i == 0 ? "invoke1" : "completed" }, new() { Event = "failure", NextStep = "compensation" }] }).ToArray()
        };
        var probe = new MetadataProbe();
        await using var sourceHost = await StartAcceptanceHost([source], target.Name, probe);
        using var sourceClient = sourceHost.Client(); using var targetClient = targetHost.Client();
        Assert.NotSame(sourceHost.App.Services.GetRequiredService<ISagaStateStore>(), targetHost.App.Services.GetRequiredService<ISagaStateStore>());
        var firstId = await StartAcceptanceSaga(sourceHost);
        var firstCommand = await ReadParticipant(target.Steps[0].Name);
        await SendParticipantOutcome(target.Domain, firstCommand, true);
        var activeCommand = await ReadParticipant(target.Steps[0].Name);
        await Wait(async () => (await RelationshipPage(sourceClient, firstId)).GetProperty("items").GetArrayLength() == 2
            && (await RelationshipPage(sourceClient, firstId)).GetProperty("items")[1].GetProperty("lastKnownStatus").GetString() == "Accepted");
        var otherId = await StartAcceptanceSaga(sourceHost, 2);
        var otherCommand = await ReadParticipant(target.Steps[0].Name);
        await Wait(async () => (await RelationshipPage(sourceClient, otherId)).GetProperty("items")[0].GetProperty("lastKnownStatus").GetString() == "Accepted");
        var firstPage = await RelationshipPage(sourceClient, firstId);
        var rows = firstPage.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, rows.Select(r => r.GetProperty("invocationId").GetString()).Distinct().Count());
        Assert.NotEqual(firstCommand.Headers.SagaInstanceId, activeCommand.Headers.SagaInstanceId);
        Assert.Equal(new[] { "Completed", "Accepted" }, rows.Select(r => r.GetProperty("lastKnownStatus").GetString()));
        foreach (var row in rows)
        {
            Assert.Equal(firstId, row.GetProperty("sourceSagaInstanceId").GetString());
            Assert.Equal(source.Domain, row.GetProperty("source").GetProperty("domain").GetString());
            Assert.Equal(source.Name, row.GetProperty("source").GetProperty("process").GetString());
            Assert.Equal(source.Version, row.GetProperty("source").GetProperty("version").GetString());
            Assert.Equal(target.Domain, row.GetProperty("target").GetProperty("domain").GetString());
            Assert.Equal(target.Name, row.GetProperty("target").GetProperty("process").GetString());
            Assert.Equal(target.Version, row.GetProperty("target").GetProperty("version").GetString());
            Assert.Equal(JsonValueKind.Null, row.GetProperty("targetCorrelationId").ValueKind);
            var incoming = (await RelationshipPage(targetClient, row.GetProperty("targetSagaInstanceId").GetString()!)).GetProperty("items")[0];
            Assert.Equal(row.GetProperty("invocationId").GetString(), incoming.GetProperty("invocationId").GetString());
            Assert.Equal(row.GetProperty("sourceCorrelationId").GetString(), incoming.GetProperty("sourceCorrelationId").GetString());
            Assert.NotEqual(incoming.GetProperty("sourceCorrelationId").GetString(), incoming.GetProperty("targetCorrelationId").GetString());
            Assert.Equal("Target", incoming.GetProperty("perspective").GetString());
            Assert.Equal(JsonValueKind.Null, incoming.GetProperty("lastKnownStatus").ValueKind);
        }
        var otherBefore = (await RelationshipPage(sourceClient, otherId)).GetRawText();
        var otherTargetBefore = (await RelationshipPage(targetClient, otherCommand.Headers.SagaInstanceId)).GetRawText();
        var graphJson = await sourceClient.GetStringAsync($"sagaflow/api/definition-graph?domain={source.Domain}&process=local&version=1.0");
        using (var graph = JsonDocument.Parse(graphJson))
        {
            Assert.Equal(3, graph.RootElement.GetProperty("edges").GetArrayLength());
            Assert.Equal(2, graph.RootElement.GetProperty("edges").EnumerateArray().Count(e => e.GetProperty("target").ValueKind != JsonValueKind.Null));
            Assert.DoesNotContain(targetHost.DiscoveryUrl.TrimEnd('/'), graphJson);
        }
        var pendingStart = StartAcceptanceSaga(sourceHost, 3);
        var metadataId = await probe.Prepared.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Process? browser = null;
        try
        {
            var legacyTemplate = SourceDefinition(target, "reconcile" + suffix);
            var legacy = new SagaDefinition { Domain = legacyTemplate.Domain, Name = legacyTemplate.Name, Version = legacyTemplate.Version,
                Steps = legacyTemplate.Steps, BigPicture = [source.BigPicture[0]] };
            await using var failedHost = await StartAcceptanceHost([legacy], target.Name);
            using var failedClient = failedHost.Client();
            await failedHost.App.Services.GetRequiredService<RabbitMqTransport>().PublisherChannel!.CloseAsync(CancellationToken.None);
            await StartAcceptanceSaga(failedHost);
            var failedId = (await failedHost.App.Services.GetRequiredService<ISagaStateStore>().ListAsync()).Single().Id;
            var failedCommand = await ReadParticipant(target.Steps[0].Name);
            await Wait(async () => (await RelationshipPage(failedClient, failedId)).GetProperty("items")[0].GetProperty("lastKnownStatus").GetString() == "Accepted");
            // Explicit reconciliation fixture: a broker failure now retains the command in Outbox.
            await failedHost.App.Services.GetRequiredService<ISagaStateStore>().UpdateAsync(failedId, state =>
            {
                var now = DateTimeOffset.UtcNow;
                state.Steps[0].Reconciliation = new("Manual reconciliation required by test fixture.", now);
                state.Steps[0].Status = SagaStepStatus.Failed;
                state.Status = SagaStatus.Failed;
                state.CurrentStep = null;
                state.FailureReason = "Manual reconciliation required by test fixture.";
                state.UpdatedAt = now;
                return true;
            });
            await failedHost.App.Services.GetRequiredService<SagaStateChangePublisher>().PublishAsync(failedId);
            // Separate high-volume read-only fixture, deliberately NOT cross-domain execution evidence.
            var pagesDefinition = new SagaDefinition { Domain = "pages" + suffix, Name = "pages", Version = "1.0", BigPicture = source.BigPicture,
                Steps = Enumerable.Range(0, 101).Select(i => new SagaStepDefinition { Name = "page" + i, Command = "InvokeRemote", External = true,
                    Transitions = [new() { Event = "success", NextStep = i < 100 ? "page" + (i + 1) : "completed" }] }).ToArray() };
            await using var pagesHost = await StartAcceptanceHost([pagesDefinition], rabbit: false);
            using var pagesClient = pagesHost.Client();
            var pagesId = Guid.NewGuid().ToString("N"); var now = DateTimeOffset.UtcNow;
            var sourceIdentity = new ProcessIdentity(pagesDefinition.Domain, "pages", "1.0");
            var targetIdentity = new ProcessIdentity(target.Domain, target.Name, target.Version);
            await pagesHost.App.Services.GetRequiredService<ISagaStateStore>().SaveAsync(new SagaInstance { Id = pagesId,
                DefinitionDomain = sourceIdentity.Domain, DefinitionName = "pages", DefinitionVersion = "1.0", CorrelationId = "isolated-pagination-correlation",
                CreatedAt = now, UpdatedAt = now, Status = SagaStatus.Running,
                Steps = pagesDefinition.Steps.Select(s => new SagaStepState { StepName = s.Name, External = true, Command = s.Command,
                    Invocation = new(new(Guid.NewGuid().ToString("N"), "isolated-pagination-correlation", sourceIdentity, pagesId, s.Name, targetIdentity), JsonSerializer.SerializeToElement(new { fixture = true })),
                    InvocationProgress = new(ExternalProcessStatus.Prepared, null, now, now, null) }).ToArray() });
            var pageOne = await RelationshipPage(pagesClient, pagesId, "?limit=100");
            var pageTwo = await RelationshipPage(pagesClient, pagesId, "?limit=100&offset=100");
            Assert.Equal(100, pageOne.GetProperty("items").GetArrayLength()); Assert.Equal(100, pageOne.GetProperty("nextOffset").GetInt32());
            Assert.Single(pageTwo.GetProperty("items").EnumerateArray()); Assert.Equal(JsonValueKind.Null, pageTwo.GetProperty("nextOffset").ValueKind);
            Assert.Equal(HttpStatusCode.BadRequest, (await pagesClient.GetAsync($"sagaflow/api/instances/{pagesId}/relationships?limit=101")).StatusCode);
            var emptyDefinition = new SagaDefinition { Domain = "empty" + suffix, Name = "empty", Version = "1.0", BigPicture = source.BigPicture, Steps = Definition("empty", "empty", "emptyStep" + suffix).Steps };
            await using var emptyHost = await StartAcceptanceHost([emptyDefinition]);
            using var emptyClient = emptyHost.Client();
            var emptyId = await StartAcceptanceSaga(emptyHost);
            Assert.Empty((await RelationshipPage(emptyClient, emptyId)).GetProperty("items").EnumerateArray());
            var evidence = Path.Combine(RepoRoot(), ".artifacts", "s26-acceptance"); Directory.CreateDirectory(evidence);
            foreach (var name in new[] { "browser-result.json", "http-result.json", "visual-review.json", "failure.png" })
            { var file = Path.Combine(evidence, name); if (File.Exists(file)) File.Delete(file); }
            foreach (var name in new[] { "complete", "reconcile", "duplicate", "cleanup" })
                foreach (var extension in new[] { ".signal", ".ack" })
                { var file = Path.Combine(evidence, name + extension); if (File.Exists(file)) File.Delete(file); }
            var manifest = new { evidence, sourceUrl = sourceHost.Url, targetUrl = targetHost.Url, failedUrl = failedHost.Url,
                pagesUrl = pagesHost.Url, emptyUrl = emptyHost.Url, firstId, otherId, metadataId, failedId, pagesId, emptyId,
                source = new { domain = source.Domain, process = source.Name, version = source.Version },
                target = new { domain = target.Domain, process = target.Name, version = target.Version },
                pages = new { domain = pagesDefinition.Domain, process = "pages", version = "1.0" },
                empty = new { domain = emptyDefinition.Domain, process = "empty", version = "1.0" },
                failed = new { domain = legacy.Domain, process = legacy.Name, version = legacy.Version },
                activeTargetId = activeCommand.Headers.SagaInstanceId, activeInvocationId = rows[1].GetProperty("invocationId").GetString(),
                firstRows = rows, otherRows = (await RelationshipPage(sourceClient, otherId)).GetProperty("items"),
                targetRow = (await RelationshipPage(targetClient, activeCommand.Headers.SagaInstanceId)).GetProperty("items")[0] };
            var manifestPath = Path.Combine(evidence, "manifest.json"); await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest));
            Task<string>? stdout = null, stderr = null;
            var useBrowser = Environment.GetEnvironmentVariable("SAGAFLOW_BIGPICTURE_BROWSER") == "true";
            if (useBrowser)
            {
                browser = Process.Start(new ProcessStartInfo("node") { WorkingDirectory = RepoRoot(), UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true, ArgumentList = { "scripts/verify-bigpicture-ux.mjs", manifestPath } })!;
                stdout = browser.StandardOutput.ReadToEndAsync(); stderr = browser.StandardError.ReadToEndAsync();
            }
            async Task Checkpoint(string name, Func<Task> action)
            {
                if (useBrowser)
                {
                    var deadline = DateTime.UtcNow.AddSeconds(150);
                    while (!File.Exists(Path.Combine(evidence, name + ".signal")) && !browser!.HasExited && DateTime.UtcNow < deadline) await Task.Delay(50);
                    Assert.True(File.Exists(Path.Combine(evidence, name + ".signal")), "Browser checkpoint " + name + ": " + (browser!.HasExited ? await stderr! : "timeout"));
                }
                await action(); await File.WriteAllTextAsync(Path.Combine(evidence, name + ".ack"), "actual host action completed");
            }
            await Checkpoint("complete", async () =>
            {
                await SendParticipantOutcome(target.Domain, activeCommand, true);
                await Wait(async () => (await ApiDetail(sourceClient, firstId)).GetProperty("status").GetString() == "Completed");
                Assert.Equal(otherBefore, (await RelationshipPage(sourceClient, otherId)).GetRawText());
                Assert.Equal(otherTargetBefore, (await RelationshipPage(targetClient, otherCommand.Headers.SagaInstanceId)).GetRawText());
            });
            await Checkpoint("duplicate", async () =>
            {
                for (var i = 0; i < 3; i++) await sourceHost.App.Services.GetRequiredService<SagaStateChangePublisher>().PublishAsync(firstId);
            });
            await Checkpoint("reconcile", async () =>
            {
                await SendParticipantOutcome(target.Domain, failedCommand, true);
                await Wait(async () => (await RelationshipPage(failedClient, failedId)).GetProperty("items")[0].GetProperty("lastKnownStatus").GetString() == "Completed");
                var detail = await ApiDetail(failedClient, failedId); Assert.Equal("Failed", detail.GetProperty("status").GetString());
                Assert.Equal(JsonValueKind.Object, detail.GetProperty("steps")[0].GetProperty("invocation").GetProperty("reconciliation").ValueKind);
            });
            await Checkpoint("cleanup", async () => await sourceHost.App.Services.GetRequiredService<SagaStateChangePublisher>().PublishAsync(firstId));
            if (browser is not null)
            {
                await browser.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(150));
                var output = await stdout!; var errors = await stderr!;
                await File.WriteAllTextAsync(Path.Combine(evidence, "browser-process.log"), output + "\n" + errors);
                Assert.True(browser.ExitCode == 0, output + "\n" + errors);
            }
            await File.WriteAllTextAsync(Path.Combine(evidence, "http-result.json"), JsonSerializer.Serialize(new { status = "PASS", hostIsolation = "Separate IHost/DI/store/connections in SAME OS process",
                firstPage, final = await RelationshipPage(sourceClient, firstId), otherBefore = JsonSerializer.Deserialize<JsonElement>(otherBefore),
                otherAfter = await RelationshipPage(sourceClient, otherId), otherTargetBefore = JsonSerializer.Deserialize<JsonElement>(otherTargetBefore),
                otherTargetAfter = await RelationshipPage(targetClient, otherCommand.Headers.SagaInstanceId), targetAfter = await RelationshipPage(targetClient, activeCommand.Headers.SagaInstanceId),
                missingMetadata = await RelationshipPage(sourceClient, metadataId), reconciliation = await RelationshipPage(failedClient, failedId),
                reconciliationSourceStatus = (await ApiDetail(failedClient, failedId)).GetProperty("status").GetString(),
                pagination = new { isolatedSeededFixture = true, pageOne, pageTwo }, browserExecuted = useBrowser }));
        }
        finally
        {
            if (browser is not null) { if (!browser.HasExited) browser.Kill(true); browser.Dispose(); }
            probe.Release.TrySetResult(); await pendingStart;
        }
    }
}
