using System.Diagnostics;
using System.Text.Json;
using RabbitMQ.Client;
using Microsoft.Extensions.DependencyInjection;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Definitions;
using SagaFlow.Core.Messages;
using SagaFlow.Core.State;
using SagaFlow.Engine.Coordination;

namespace SagaFlow.Messaging.RabbitMQ.Tests;
public sealed partial class ProcessInvocationIntegrationTests
{
    private sealed class MetadataProbe
    {
        public TaskCompletionSource<string> Prepared { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class PauseMetadata(MetadataProbe probe) : ILocalSagaStepHandler
    {
        public async Task ExecuteAsync(SagaCommand command, CancellationToken ct)
        {
            if (!command.Payload.TryGetProperty("reference", out var reference) || reference.GetInt32() != 3) return;
            probe.Prepared.TrySetResult(command.Headers.SagaInstanceId);
            await probe.Release.Task.WaitAsync(ct);
        }
    }
    [RabbitMqFact]
    public async Task MultipleInvocationHttpIsolationAndOptionalRealBrowserAcceptance()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var target = Definition("target" + suffix, "remote", "targetStep" + suffix);
        var source = new SagaDefinition { Domain = "source" + suffix, Name = "local", Version = "1.0",
            BigPicture = [new() { Domain = target.Domain, Process = target.Name, Version = target.Version, Endpoint = "https://definition-only", Relationship = "invokes" }],
            Steps = [new() { Name = "invoke" + suffix, Command = "InvokeRemote", External = true,
                Transitions = [new() { Event = "success", NextStep = "completed" }, new() { Event = "failure", NextStep = "compensation" }] }] };
        await using var targetHost = await StartDashboard(target);
        var probe = new MetadataProbe();
        await using var sourceHost = await StartDashboard(source, target.Name, services =>
        { services.AddSingleton(probe); services.AddScoped<ILocalSagaStepHandler, PauseMetadata>(); });
        using var sourceClient = DashboardClient(sourceHost); using var targetClient = DashboardClient(targetHost);
        async Task<string> StartSource(int reference)
        {
            await using var scope = sourceHost.Services.CreateAsyncScope();
            return (await scope.ServiceProvider.GetRequiredService<ISagaFlow>().StartAsync(new { allow = true, reference })).ToString("N");
        }
        var ids = new[] { await StartSource(1), await StartSource(2) };
        await Wait(async () =>
        {
            foreach (var id in ids)
                if ((await ApiDetail(sourceClient, id)).GetProperty("steps")[0].GetProperty("invocation").GetProperty("lastKnownStatus").GetString() != "Accepted") return false;
            return true;
        });
        var before = new[] { await ApiDetail(sourceClient, ids[0]), await ApiDetail(sourceClient, ids[1]) };
        var outgoing = before.Select(d => d.GetProperty("steps")[0].GetProperty("invocation")).ToArray();
        var targetIds = outgoing.Select(d => d.GetProperty("targetSagaInstanceId").GetString()!).ToArray();
        var targets = new[] { await ApiDetail(targetClient, targetIds[0]), await ApiDetail(targetClient, targetIds[1]) };
        var secondRelationshipBefore = await sourceClient.GetStringAsync($"/sagaflow/api/instances/{ids[1]}/relationships");
        var secondTargetRelationshipBefore = await targetClient.GetStringAsync($"/sagaflow/api/instances/{targetIds[1]}/relationships");
        Assert.NotEqual(ids[0], ids[1]); Assert.NotEqual(targetIds[0], targetIds[1]);
        Assert.NotEqual(outgoing[0].GetProperty("invocationId").GetString(), outgoing[1].GetProperty("invocationId").GetString());
        Assert.NotEqual(before[0].GetProperty("correlationId").GetString(), before[1].GetProperty("correlationId").GetString());
        Assert.NotEqual(targets[0].GetProperty("correlationId").GetString(), targets[1].GetProperty("correlationId").GetString());
        for (var i = 0; i < 2; i++)
        {
            var origin = targets[i].GetProperty("incomingInvocation");
            Assert.Equal(ids[i], origin.GetProperty("sourceSagaInstanceId").GetString());
            Assert.Equal(outgoing[i].GetProperty("invocationId").GetString(), origin.GetProperty("invocationId").GetString());
            Assert.Equal(before[i].GetProperty("correlationId").GetString(), origin.GetProperty("sourceCorrelationId").GetString());
            Assert.NotEqual(before[i].GetProperty("correlationId").GetString(), targets[i].GetProperty("correlationId").GetString());
            Assert.Equal(source.Domain, origin.GetProperty("source").GetProperty("domain").GetString());
            Assert.Equal(target.Domain, outgoing[i].GetProperty("target").GetProperty("domain").GetString());
            Assert.Equal(target.Name, outgoing[i].GetProperty("target").GetProperty("process").GetString());
            Assert.Equal(target.Version, outgoing[i].GetProperty("target").GetProperty("version").GetString());
            Assert.Equal(source.Name, origin.GetProperty("source").GetProperty("process").GetString());
            Assert.Equal(source.Version, origin.GetProperty("source").GetProperty("version").GetString());
            Assert.Equal(JsonValueKind.Null, outgoing[i].GetProperty("reconciliation").ValueKind);
            Assert.Equal("Local", before[i].GetProperty("origin").GetString());
            Assert.Equal(JsonValueKind.Null, before[i].GetProperty("incomingInvocation").ValueKind);
        }
        var commands = new[] { await ReadParticipant(target.Steps[0].Name), await ReadParticipant(target.Steps[0].Name) };
        var firstCommand = commands.Single(c => c.Headers.SagaInstanceId == targetIds[0]);
        var pendingStart = StartSource(3);
        var metadataId = await probe.Prepared.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var missing = await ApiDetail(sourceClient, metadataId);
            var missingRelation = missing.GetProperty("steps")[0].GetProperty("invocation");
            Assert.Equal(JsonValueKind.Null, missingRelation.GetProperty("targetSagaInstanceId").ValueKind);
            Assert.Equal(JsonValueKind.Null, missingRelation.GetProperty("lastObservedAt").ValueKind);
            Assert.Equal(JsonValueKind.Null, missingRelation.GetProperty("reconciliation").ValueKind);
            Assert.Equal(JsonValueKind.Null, missing.GetProperty("incomingInvocation").ValueKind);
            var root = RepoRoot(); var evidence = Path.Combine(root, ".artifacts", "s25-acceptance");
            Directory.CreateDirectory(evidence);
            var signal = Path.Combine(evidence, "complete-first.signal");
            if (File.Exists(signal)) File.Delete(signal);
            var legacy = SourceDefinition(target, "legacy" + suffix);
            await using var failedHost = await StartDashboard(legacy, target.Name);
            using var failedClient = DashboardClient(failedHost);
            await failedHost.Services.GetRequiredService<RabbitMqTransport>().PublisherChannel!.CloseAsync(CancellationToken.None);
            await using (var scope = failedHost.Services.CreateAsyncScope())
                await Assert.ThrowsAsync<SagaDispatchException>(() => scope.ServiceProvider.GetRequiredService<ISagaFlow>().StartAsync(new { allow = true }));
            var failedId = (await failedHost.Services.GetRequiredService<ISagaStateStore>().ListAsync()).Single().Id;
            Process? browser = null; Task<string>? stdout = null, stderr = null;
            if (Environment.GetEnvironmentVariable("SAGAFLOW_BROWSER_ACCEPTANCE") == "true")
            {
                var manifest = new { sourceUrl = sourceClient.BaseAddress!.ToString(), targetUrl = targetClient.BaseAddress!.ToString(),
                    failedUrl = failedClient.BaseAddress!.ToString(), failedId, firstId = ids[0], secondId = ids[1], metadataId,
                    firstTargetId = targetIds[0], invocationId = outgoing[0].GetProperty("invocationId").GetString(), signal,
                    sourceCorrelation = before[0].GetProperty("correlationId").GetString(), targetCorrelation = targets[0].GetProperty("correlationId").GetString(), evidence };
                var manifestPath = Path.Combine(evidence, "manifest.json");
                await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest));
                browser = Process.Start(new ProcessStartInfo("node") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true, ArgumentList = { "scripts/verify-instance-ux.mjs", manifestPath } })!;
                stdout = browser.StandardOutput.ReadToEndAsync(); stderr = browser.StandardError.ReadToEndAsync();
                var limit = DateTime.UtcNow.AddSeconds(90);
                while (!File.Exists(signal) && !browser.HasExited && DateTime.UtcNow < limit) await Task.Delay(25);
                Assert.True(File.Exists(signal), "Browser did not reach live-update checkpoint: " + (browser.HasExited ? await stderr : "timeout"));
            }
            await SendParticipantOutcome(target.Domain, firstCommand, true);
            await Wait(async () => (await ApiDetail(sourceClient, ids[0])).GetProperty("status").GetString() == "Completed");
            var secondAfter = await ApiDetail(sourceClient, ids[1]); var secondTargetAfter = await ApiDetail(targetClient, targetIds[1]);
            Assert.Equal(before[1].GetRawText(), secondAfter.GetRawText());
            Assert.Equal(targets[1].GetRawText(), secondTargetAfter.GetRawText());
            Assert.Equal(secondRelationshipBefore, await sourceClient.GetStringAsync($"/sagaflow/api/instances/{ids[1]}/relationships"));
            Assert.Equal(secondTargetRelationshipBefore, await targetClient.GetStringAsync($"/sagaflow/api/instances/{targetIds[1]}/relationships"));
            await File.WriteAllTextAsync(Path.Combine(evidence, "isolation.json"), JsonSerializer.Serialize(new { before, targets, secondAfter, secondTargetAfter, missing }));
            if (browser is not null)
            {
                await browser.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
                Assert.True(browser.ExitCode == 0, (await stdout!) + "\n" + (await stderr!));
                browser.Dispose();
            }
        }
        finally { probe.Release.TrySetResult(); await pendingStart; }
        await sourceHost.StopAsync(); await targetHost.StopAsync();
    }
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SagaFlow.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("SagaFlow repository root not found.");
    }
}
