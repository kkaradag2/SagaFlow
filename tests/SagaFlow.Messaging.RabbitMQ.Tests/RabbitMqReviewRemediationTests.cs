using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Messages;
using SagaFlow.Core.State;
using SagaFlow.Storage.InMemory;
using SagaFlow.RabbitMq.Protocol;

namespace SagaFlow.Messaging.RabbitMQ.Tests;

public sealed partial class RabbitMqTransportIntegrationTests
{
    [RabbitMqFact]
    public async Task LegacyAndUnsupportedOutcomeVersionsArePreservedWithoutApplyingStateAndV1Continues()
    {
        await using var runtime = await StartExternalAsync();
        var id = await StartSagaAsync(runtime.Host, new { product = "sku", quantity = 3 });
        var command = await GetMessageAsync(runtime.Topology.CommandQueue);
        var before = (await FindSagaAsync(runtime.Host, id))!;
        var transport = runtime.Host.Services.GetRequiredService<RabbitMqTransport>();
        var quarantine = RabbitMqQuarantine.Queue(runtime.Topology.OutcomeQueue);
        object?[] versions = [null, "0", "2", "", 1, new byte[] { 0xff }];
        for (var index = 0; index < versions.Length; index++)
        {
            var body = Encoding.UTF8.GetBytes("{\"name\":\"Legacy\",\"payload\":{\"invoice\":17},\"isSuccess\":true,\"headers\":{}}");
            var properties = new BasicProperties { MessageId = "legacy-" + index, CorrelationId = "legacy-correlation", ReplyTo = "legacy-reply",
                Type = index % 2 == 0 ? "SagaOutcomeEvent" : "SagaCommand", ContentType = "application/json", Headers = new Dictionary<string, object?>() };
            if (versions[index] is { } version) properties.Headers["sf-version"] = version;
            await PublishRawAsync(runtime.Topology.OutcomeQueue, body, properties);
            await WaitUntilAsync(() => Task.FromResult(transport.QuarantineAcknowledgementCount == index + 1));
            var preserved = await GetMessageAsync(quarantine);
            Assert.Equal(body, preserved.Body.ToArray());
            Assert.Equal(properties.MessageId, preserved.BasicProperties.MessageId);
            Assert.Equal(properties.Type, preserved.BasicProperties.Type);
            Assert.Equal(properties.CorrelationId, preserved.BasicProperties.CorrelationId);
            Assert.Equal(properties.ReplyTo, preserved.BasicProperties.ReplyTo);
            Assert.Equal(runtime.Topology.OutcomeQueue,
                Encoding.UTF8.GetString((byte[])preserved.BasicProperties.Headers!["sf-quarantine-source"]!));
            Assert.Equal(before.Revision, (await FindSagaAsync(runtime.Host, id))!.Revision);
            Assert.Equal(0u, await MessageCountAsync(runtime.Topology.OutcomeQueue));
            Assert.Equal(0u, await ConsumerCountAsync(quarantine));
        }
        Assert.Equal(0, transport.QuarantineFailureCount);
        Assert.Equal(1u, await ConsumerCountAsync(runtime.Topology.OutcomeQueue));
        await PublishRawAsync(runtime.Topology.OutcomeQueue, "{\"businessResult\":17}"u8.ToArray(), EchoOutcome(command));
        await WaitUntilAsync(async () => (await FindSagaAsync(runtime.Host, id))?.Status == SagaStatus.Completed);
    }

    [RabbitMqFact]
    public async Task QuarantinePublishFailureRetainsOriginalAndStopsConsumerWithoutHotRetry()
    {
        await using var runtime = await StartExternalAsync();
        var id = await StartSagaAsync(runtime.Host, new { invoice = 9 });
        _ = await GetMessageAsync(runtime.Topology.CommandQueue);
        var before = (await FindSagaAsync(runtime.Host, id))!;
        var transport = runtime.Host.Services.GetRequiredService<RabbitMqTransport>();
        var quarantine = RabbitMqQuarantine.Queue(runtime.Topology.OutcomeQueue);
        await using var inspection = await broker.Connection.CreateChannelAsync();
        await inspection.QueueDeleteAsync(quarantine);
        var properties = new BasicProperties { MessageId = "retained-legacy", ContentType = "application/json", Type = "SagaOutcomeEvent" };
        await PublishRawAsync(runtime.Topology.OutcomeQueue, "{\"legacy\":true}"u8.ToArray(), properties);
        await WaitUntilAsync(() => Task.FromResult(transport.QuarantineFailureCount == 1 && transport.ConsumerChannel is null));
        Assert.Equal(0, transport.QuarantineAcknowledgementCount);
        Assert.Equal(0u, await ConsumerCountAsync(runtime.Topology.OutcomeQueue));
        Assert.Equal(1u, await MessageCountAsync(runtime.Topology.OutcomeQueue));
        await Task.Delay(250);
        Assert.Equal(1, transport.QuarantineFailureCount);
        var retained = await inspection.BasicGetAsync(runtime.Topology.OutcomeQueue, false);
        Assert.NotNull(retained); Assert.True(retained.Redelivered);
        Assert.Equal("retained-legacy", retained.BasicProperties.MessageId);
        Assert.Equal("{\"legacy\":true}", Encoding.UTF8.GetString(retained.Body.Span));
        await inspection.BasicNackAsync(retained.DeliveryTag, false, true);
        Assert.Equal(before.Revision, (await FindSagaAsync(runtime.Host, id))!.Revision);
        // Restore topology for explicit operator recovery, with no automatic replay into SagaFlow.
        await RabbitMqQuarantine.DeclareAsync(inspection, runtime.Topology.OutcomeQueue, CancellationToken.None);
        await runtime.StopAsync();
        Assert.Equal(1u, await MessageCountAsync(runtime.Topology.OutcomeQueue));
    }

    [RabbitMqFact]
    public async Task ConfirmedTransferWithoutSourceAckCanPreserveDuplicatesAndNeverReapplySaga()
    {
        await using var runtime = await StartExternalAsync();
        var id = await StartSagaAsync(runtime.Host, new { invoice = 12 });
        _ = await GetMessageAsync(runtime.Topology.CommandQueue);
        var before = (await FindSagaAsync(runtime.Host, id))!;
        await runtime.StopAsync();
        var queue = runtime.Topology.OutcomeQueue;
        var quarantine = RabbitMqQuarantine.Queue(queue);
        var body = "{\"isSuccess\":true,\"legacy\":12}"u8.ToArray();
        await PublishRawAsync(queue, body, new() { MessageId = "same-logical-legacy", Type = "SagaOutcomeEvent", ContentType = "application/json" });
        await using var publisher = await broker.Connection.CreateChannelAsync(new CreateChannelOptions(true, true));
        await using (var source = await broker.Connection.CreateChannelAsync())
        {
            var first = (await source.BasicGetAsync(queue, false))!;
            await RabbitMqQuarantine.TransferAsync(publisher, queue, first.Exchange, first.RoutingKey, first.BasicProperties, first.Body, CancellationToken.None);
            Assert.Equal(1u, await MessageCountAsync(quarantine));
            // Simulate connection/channel loss after the real confirm but before the source ACK.
            await source.CloseAsync();
        }
        await using (var source = await broker.Connection.CreateChannelAsync())
        {
            var again = (await source.BasicGetAsync(queue, false))!;
            Assert.True(again.Redelivered);
            await RabbitMqQuarantine.TransferAsync(publisher, queue, again.Exchange, again.RoutingKey, again.BasicProperties, again.Body, CancellationToken.None);
            await source.BasicAckAsync(again.DeliveryTag, false);
        }
        Assert.Equal(0u, await MessageCountAsync(queue));
        Assert.Equal(2u, await MessageCountAsync(quarantine));
        Assert.Equal(0u, await ConsumerCountAsync(quarantine));
        foreach (var _ in Enumerable.Range(0, 2))
        {
            var copy = await GetMessageAsync(quarantine);
            Assert.Equal("same-logical-legacy", copy.BasicProperties.MessageId);
            Assert.Equal(body, copy.Body.ToArray());
        }
        Assert.Equal(before.Revision, (await FindSagaAsync(runtime.Host, id))!.Revision);
    }

    [RabbitMqFact]
    public async Task ConcurrentBrokerOutcomesEnterTwoConsumersAndAtomicallyAdvanceOnlyOnce()
    {
        var store = new OverlappingOutcomeStore();
        await using var first = await StartExternalAsync(store: store, definitionYaml: WireDefinition);
        await using var second = await StartExternalAsync(first.Topology, store: store, definitionYaml: WireDefinition, cleanupTopology: false);
        Assert.Equal(2u, await ConsumerCountAsync(first.Topology.OutcomeQueue));
        var id = await StartSagaAsync(first.Host, new { businessId = 42 });
        var command = await GetMessageAsync("work");
        var before = (await FindSagaAsync(first.Host, id))!;
        store.BlockNextTwoUpdates(id.ToString("N"));
        try
        {
            // Independent confirm-enabled publisher channels issue equivalent messages concurrently.
            var outcome = EchoOutcome(command);
            var sameMessageId = outcome.MessageId;
            var equivalent = EchoOutcome(command); equivalent.MessageId = sameMessageId;
            await Task.WhenAll(PublishRawAsync(first.Topology.OutcomeQueue, "{\"reserved\":true}"u8.ToArray(), outcome),
                PublishRawAsync(first.Topology.OutcomeQueue, "{\"reserved\":true}"u8.ToArray(), equivalent));
            await store.BothEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2, store.ConcurrentEntries);
        }
        finally { store.Release.TrySetResult(); }
        var transports = new[] { first.Host.Services.GetRequiredService<RabbitMqTransport>(), second.Host.Services.GetRequiredService<RabbitMqTransport>() };
        await WaitUntilAsync(() => Task.FromResult(transports.Sum(t => t.DuplicateAcknowledgementCount) == 1));
        await WaitForConsumerAsync(first.Topology.OutcomeQueue);
        var after = (await FindSagaAsync(first.Host, id))!;
        Assert.Equal(before.Revision + 1, after.Revision);
        Assert.Equal(SagaStepStatus.Completed, after.Steps[0].Status);
        Assert.NotNull(after.Steps[0].Result);
        Assert.Equal(SagaStepStatus.Dispatched, after.Steps[1].Status);
        Assert.Null(after.Steps[1].Result);
        Assert.Equal(1, after.Steps.Count(step => step.CompletedAt.HasValue));
        Assert.Equal(1u, await MessageCountAsync("nextWork"));
        foreach (var bad in new[] { "correlation", "execution" })
        {
            var forged = EchoOutcome(command);
            if (bad == "correlation")
            {
                forged.CorrelationId = "foreign"; forged.Headers!["sf-ex-correlation-id"] = "foreign";
            }
            else forged.Headers!["sf-ex-execution-id"] = "foreign";
            await PublishRawAsync(first.Topology.OutcomeQueue, "{}"u8.ToArray(), forged);
        }
        await WaitForConsumerAsync(first.Topology.OutcomeQueue);
        Assert.Equal(after.Revision, (await FindSagaAsync(first.Host, id))!.Revision);
        Assert.Equal(1u, await MessageCountAsync("nextWork"));
        Assert.Equal(1, transports.Sum(t => t.DuplicateAcknowledgementCount));
        // Inspect and consume the one deliberately pending command so another fixture cannot read it.
        var onlyNext = await GetMessageAsync("nextWork");
        Assert.Equal(after.Steps[1].Execution, RabbitMqMessageSerializer.DeserializeCommand(onlyNext).Headers);
        AssertBusinessBody(onlyNext, JsonSerializer.SerializeToElement(new { businessId = 42 }));
        Assert.Equal(0u, await MessageCountAsync("nextWork"));
    }

    private sealed class OverlappingOutcomeStore : ISagaStateStore
    {
        private readonly InMemorySagaStateStore inner = new();
        private string? blockedInstance;
        private int remaining;
        private int entries;
        public int ConcurrentEntries => Volatile.Read(ref entries);
        public TaskCompletionSource BothEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void BlockNextTwoUpdates(string instance) { blockedInstance = instance; remaining = 2; }
        public ValueTask SaveAsync(SagaInstance value, CancellationToken ct = default) => inner.SaveAsync(value, ct);
        public ValueTask<SagaInstance?> FindAsync(string id, CancellationToken ct = default) => inner.FindAsync(id, ct);
        public ValueTask<IReadOnlyList<SagaInstance>> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);
        public async ValueTask<T> UpdateAsync<T>(string id, Func<SagaInstance, T> update, CancellationToken ct = default)
        {
            if (id == blockedInstance && Interlocked.Decrement(ref remaining) >= 0)
            {
                if (Interlocked.Increment(ref entries) == 2) BothEntered.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            }
            return await inner.UpdateAsync(id, update, ct);
        }
        public async ValueTask<T> UpdateWithOutboxAsync<T>(string id, Func<SagaInstance, (T Result, OutboxMessage? Message)> update, CancellationToken ct = default)
        {
            if (id == blockedInstance && Interlocked.Decrement(ref remaining) >= 0)
            {
                if (Interlocked.Increment(ref entries) == 2) BothEntered.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            }
            return await inner.UpdateWithOutboxAsync(id, update, ct);
        }
        public ValueTask<IReadOnlyList<OutboxClaim>> ClaimOutboxAsync(string owner, DateTimeOffset now, TimeSpan lease, int limit, CancellationToken ct = default) => inner.ClaimOutboxAsync(owner, now, lease, limit, ct);
        public ValueTask MarkOutboxPublishedAsync(string id, string owner, DateTimeOffset at, CancellationToken ct = default) => inner.MarkOutboxPublishedAsync(id, owner, at, ct);
        public ValueTask ReleaseOutboxAsync(string id, string owner, DateTimeOffset at, CancellationToken ct = default) => inner.ReleaseOutboxAsync(id, owner, at, ct);
    }
}
