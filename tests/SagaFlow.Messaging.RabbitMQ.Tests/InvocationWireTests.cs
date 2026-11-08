using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Messages;

namespace SagaFlow.Messaging.RabbitMQ.Tests;

public sealed partial class ProcessInvocationIntegrationTests
{
    [RabbitMqFact]
    public async Task InvocationWireSeparatesBusinessRequestAndAllLifecycleMetadata()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var target = Definition("target" + suffix, "remote", "targetStep" + suffix);
        var source = new SagaFlow.Core.Definitions.SagaDefinition { Domain = "source" + suffix, Name = "local", Version = "1.0",
            Steps = Definition("source", "local", "sourceStep" + suffix).Steps,
            BigPicture = [new() { Domain = target.Domain, Process = target.Name, Version = target.Version, Endpoint = "https://discovery", Relationship = "invokes" }] };
        using var targetHost = await Start(target);
        using var sourceHost = await Start(source, target.Name);
        var sourceIdentity = new ProcessIdentity(source.Domain, source.Name, source.Version);
        var targetIdentity = new ProcessIdentity(target.Domain, target.Name, target.Version);
        await using var channel = await broker.Connection.CreateChannelAsync(new CreateChannelOptions(true, true));
        var tap = (await channel.QueueDeclareAsync()).QueueName;
        await channel.QueueBindAsync(tap, RabbitMqInvocationTransport.Exchange, RabbitMqInvocationTransport.Queue(targetIdentity, true));
        await channel.QueueBindAsync(tap, RabbitMqInvocationTransport.Exchange, RabbitMqInvocationTransport.Queue(sourceIdentity, false));
        async Task<BasicGetResult> Next()
        {
            BasicGetResult? delivery = null;
            await Wait(async () => (delivery = await channel.BasicGetAsync(tap, true)) is not null);
            return delivery!;
        }
        var business = JsonSerializer.SerializeToElement(new { allow = true, invoice = new { number = 42 }, products = new[] { "a", "b" } });
        await using (var scope = sourceHost.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ISagaFlow>().StartAsync(business);
        var requestWire = await Next();
        var request = RabbitMqInvocationSerializer.Read<ProcessInvocationRequest>(requestWire.Body.Span, requestWire.BasicProperties);
        using (var document = JsonDocument.Parse(requestWire.Body)) Assert.True(JsonElement.DeepEquals(business, document.RootElement));
        Assert.NotNull(request.Identity.SourceExecutionId);
        Assert.False(requestWire.BasicProperties.Headers!.ContainsKey("sf-inv-target-saga"));
        Assert.Equal(RabbitMqInvocationTransport.Queue(sourceIdentity, false), requestWire.BasicProperties.ReplyTo);
        var acceptedWire = await Next();
        var accepted = RabbitMqInvocationSerializer.Read<ProcessInvocationAccepted>(acceptedWire.Body.Span, acceptedWire.BasicProperties);
        Assert.Equal("null", System.Text.Encoding.UTF8.GetString(acceptedWire.Body.Span));
        Assert.Equal(request.Identity, accepted.Message.Identity);
        Assert.Equal(request.Message.MessageId, accepted.Message.CausationId);
        Assert.NotEqual(request.Message.MessageId, accepted.Message.MessageId);
        var bus = targetHost.Services.GetRequiredService<IProcessInvocationBus>();
        foreach (var status in new[] { ExternalProcessStatus.Completed, ExternalProcessStatus.Failed, ExternalProcessStatus.Compensated })
        {
            var outcome = new ProcessInvocationTerminalOutcome(new(request.Identity, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow), accepted.TargetSagaInstanceId, status);
            await bus.PublishAsync(outcome);
            var wire = await Next();
            Assert.Equal("null", System.Text.Encoding.UTF8.GetString(wire.Body.Span));
            Assert.Equal(outcome, RabbitMqInvocationSerializer.Read<ProcessInvocationTerminalOutcome>(wire.Body.Span, wire.BasicProperties));
            Assert.Null(wire.BasicProperties.ReplyTo);
        }
        var rejection = new ProcessInvocationRejected(new(request.Identity, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow), "business rejection");
        await bus.PublishAsync(rejection);
        var rejectedWire = await Next();
        Assert.Equal("null", System.Text.Encoding.UTF8.GetString(rejectedWire.Body.Span));
        Assert.Equal(rejection, RabbitMqInvocationSerializer.Read<ProcessInvocationRejected>(rejectedWire.Body.Span, rejectedWire.BasicProperties));
        foreach (var key in requestWire.BasicProperties.Headers!.Keys.Where(key => key.StartsWith("sf-inv-", StringComparison.Ordinal)))
        {
            var invalid = new BasicProperties(requestWire.BasicProperties) { Headers = requestWire.BasicProperties.Headers.ToDictionary(p => p.Key, p => p.Value) };
            invalid.Headers.Remove(key);
            if (key == "sf-inv-source-execution" || key == "sf-inv-causation-id") continue;
            Assert.ThrowsAny<Exception>(() => RabbitMqInvocationSerializer.Read<ProcessInvocationRequest>(requestWire.Body.Span, invalid));
        }
        var transport = sourceHost.Services.GetRequiredService<RabbitMqInvocationTransport>();
        var rejectedCount = transport.RejectedMessageCount;
        var invalidWire = new BasicProperties(acceptedWire.BasicProperties) { Headers = acceptedWire.BasicProperties.Headers!.ToDictionary(p => p.Key, p => p.Value) };
        invalidWire.Headers.Remove("sf-inv-source-step");
        await channel.BasicPublishAsync("", RabbitMqInvocationTransport.Queue(sourceIdentity, false), true, invalidWire, "null"u8.ToArray());
        await Wait(() => Task.FromResult(transport.RejectedMessageCount > rejectedCount));
        var legacy = "{\"Message\":{},\"Status\":\"Completed\"}"u8.ToArray();
        var queue = RabbitMqInvocationTransport.Queue(sourceIdentity, false);
        await channel.BasicPublishAsync("", queue, true, new BasicProperties { Type = nameof(ProcessInvocationTerminalOutcome), MessageId = "legacy" }, legacy);
        await Wait(async () => await channel.MessageCountAsync(queue + ".quarantine") == 1);
        Assert.Equal(legacy, (await channel.BasicGetAsync(queue + ".quarantine", true))!.Body.ToArray());
        await sourceHost.StopAsync(); await targetHost.StopAsync();
    }
}
