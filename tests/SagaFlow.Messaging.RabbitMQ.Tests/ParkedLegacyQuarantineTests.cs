using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Messages;
using SagaFlow.Engine.Messaging;

namespace SagaFlow.Messaging.RabbitMQ.Tests;
public sealed partial class ProcessInvocationIntegrationTests
{
    [RabbitMqFact]
    public async Task ParkedLegacyQuarantineFailureAndCancellationRequeueForExplicitRetryWithoutRestart()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var target = Definition("target" + suffix, "remote", "targetStep" + suffix);
        var source = SourceDefinition(target, suffix);
        using var host = await Start(source, target.Name);
        try
        {
            await using var scope = host.Services.CreateAsyncScope();
            var failure = await Assert.ThrowsAsync<ProcessInvocationPublishException>(() => scope.ServiceProvider.GetRequiredService<ISagaFlow>().StartAsync(new { allow = true }));
            var store = host.Services.GetRequiredService<ISagaStateStore>();
            var before = (await store.FindAsync(failure.SourceInstanceId))!;
            var identity = before.Steps[0].Invocation!.Identity;
            var transport = host.Services.GetRequiredService<RabbitMqInvocationTransport>();
            var deferred = RabbitMqInvocationTransport.Queue(identity.Source, false) + ".deferred";
            var quarantine = deferred + ".quarantine";
            var legacy = new ProcessInvocationTerminalOutcome(new(identity, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow), "legacy-target", ExternalProcessStatus.Completed);
            var body = JsonSerializer.SerializeToUtf8Bytes(legacy);
            var properties = new BasicProperties { ContentType = "application/json", MessageId = legacy.Message.MessageId,
                Type = nameof(ProcessInvocationTerminalOutcome), CorrelationId = identity.CorrelationId };
            await using var channel = await broker.Connection.CreateChannelAsync(new CreateChannelOptions(true, true));
            await channel.BasicPublishAsync("", deferred, true, properties, body);
            await channel.QueueDeleteAsync(quarantine);
            var publishError = await Assert.ThrowsAsync<PublishReturnException>(() => transport.ReconcileDeferredAsync().AsTask());
            Assert.Contains("312 NO_ROUTE", publishError.Message);
            Assert.Equal(1u, await transport.GetDeferredCountAsync());
            var failedAttempts = transport.DeferredReplayCount;
            await Task.Delay(250);
            Assert.Equal(failedAttempts, transport.DeferredReplayCount); // No active retry loop.
            await channel.QueueDeclareAsync(quarantine, true, false, false);

            // Cancel after the broker delivery is acquired, while publication waits for its real gate.
            var gate = (SemaphoreSlim)typeof(RabbitMqInvocationTransport).GetField("publishing",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(transport)!;
            await gate.WaitAsync();
            try
            {
                using var cancel = new CancellationTokenSource();
                var retry = transport.ReconcileDeferredAsync(cancellationToken: cancel.Token).AsTask();
                await Wait(() => Task.FromResult(transport.DeferredReplayCount == failedAttempts + 1));
                cancel.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retry);
                Assert.Equal(1u, await transport.GetDeferredCountAsync());
                Assert.Equal(0u, await channel.MessageCountAsync(quarantine));
            }
            finally { gate.Release(); }
            Assert.Equal(0, await transport.ReconcileDeferredAsync()); // Quarantined, never applied to Saga.
            Assert.Equal(0u, await transport.GetDeferredCountAsync());
            Assert.Equal(1u, await channel.MessageCountAsync(quarantine));
            // A possible duplicate transfer preserves copies without creating coordination mutations.
            await channel.BasicPublishAsync("", deferred, true, properties, body);
            Assert.Equal(0, await transport.ReconcileDeferredAsync());
            Assert.Equal(2u, await channel.MessageCountAsync(quarantine));
            foreach (var _ in Enumerable.Range(0, 2))
            {
                var copy = (await channel.BasicGetAsync(quarantine, true))!;
                Assert.Equal(body, copy.Body.ToArray());
                Assert.Equal(legacy.Message.MessageId, copy.BasicProperties.MessageId);
            }
            Assert.Equal(0u, await transport.GetDeferredCountAsync());
            Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await store.FindAsync(before.Id)));
        }
        finally { await host.StopAsync(); }
    }
}
