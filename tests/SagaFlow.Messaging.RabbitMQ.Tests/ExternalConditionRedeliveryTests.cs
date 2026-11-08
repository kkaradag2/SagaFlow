using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Definitions;
using SagaFlow.Core.Messages;
using SagaFlow.Core.State;
using SagaFlow.Engine.ProcessDefinition;

namespace SagaFlow.Messaging.RabbitMQ.Tests;

public sealed partial class ProcessInvocationIntegrationTests
{
    [RabbitMqFact]
    public async Task RedeliveredTerminalWithInvalidExternalConditionIsAcknowledgedAndReconciledWithoutLoop()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var target = Definition("target" + suffix, "remote", "work" + suffix);
        var next = "continue" + suffix;
        var source = new SagaDefinition { Domain = "source" + suffix, Name = "local", Version = "1.0",
            BigPicture = [new() { Domain = target.Domain, Process = target.Name, Version = target.Version, Endpoint = "https://discovery", Relationship = "invokes" }],
            Steps = [new() { Name = "invoke", Command = "InvokeRemote", External = true,
                Transitions = [new() { Event = "success", NextStep = next }, new() { Event = "failure", NextStep = "compensation" }] },
                new() { Name = next, Command = "Continue", Transitions = [new() { Event = "success", NextStep = "completed" }] }]
        };
        using var targetHost = await Start(target);
        using var sourceHost = await Start(source, target.Name);
        try
        {
            string id;
            await using (var scope = sourceHost.Services.CreateAsyncScope())
                id = (await scope.ServiceProvider.GetRequiredService<ISagaFlow>().StartAsync(new { allow = true })).ToString("N");
            var store = sourceHost.Services.GetRequiredService<ISagaStateStore>();
            await Wait(async () => (await store.FindAsync(id))!.Steps[0].InvocationProgress!.Status == ExternalProcessStatus.Accepted);
            var transport = sourceHost.Services.GetRequiredService<RabbitMqInvocationTransport>();
            var channel = (IChannel)typeof(RabbitMqInvocationTransport).GetField("consumer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(transport)!;
            var tags = (List<string>)typeof(RabbitMqInvocationTransport).GetField("tags", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(transport)!;
            foreach (var tag in tags.ToArray()) await channel.BasicCancelAsync(tag);
            var definition = sourceHost.Services.GetRequiredService<ProcessDefinitionRegistry>().Find(new(source.Domain, source.Name, source.Version))!.Definition;
            // Model an old/bypassed accepted definition; keep production registration validation enabled.
            typeof(SagaTransitionDefinition).GetProperty(nameof(SagaTransitionDefinition.Condition))!.SetValue(
                definition.Steps[0].Transitions[0], new SagaConditionDefinition
                { Path = "result.status", Operator = "equals", Value = JsonSerializer.SerializeToElement("Completed") });
            await SendParticipantOutcome(target.Domain, await ReadParticipant(target.Steps[0].Name), true);
            var targetStore = targetHost.Services.GetRequiredService<ISagaStateStore>();
            await Wait(async () => (await targetStore.ListAsync()).Single().InvocationTerminalOutcome is not null);
            var terminal = (await targetStore.ListAsync()).Single().InvocationTerminalOutcome!;
            var queue = RabbitMqInvocationTransport.Queue(new(source.Domain, source.Name, source.Version), false);
            await using var probe = await broker.Connection.CreateChannelAsync();
            BasicGetResult? first = null;
            await Wait(async () => (first = await probe.BasicGetAsync(queue, false)) is not null);
            Assert.Equal("null", System.Text.Encoding.UTF8.GetString(first!.Body.Span));
            Assert.Equal("1", System.Text.Encoding.UTF8.GetString((byte[])first.BasicProperties.Headers!["sf-version"]!));
            await probe.BasicNackAsync(first.DeliveryTag, false, true); // Real broker redelivery, not a duplicate publish.
            var receive = (Func<BasicDeliverEventArgs, bool, Task>)typeof(RabbitMqInvocationTransport)
                .GetMethod("ReceiveAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate(typeof(Func<BasicDeliverEventArgs, bool, Task>), transport);
            var completedDeliveries = 0;
            var seen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += async (_, delivery) => { await receive(delivery, false); Interlocked.Increment(ref completedDeliveries); seen.TrySetResult(delivery.Redelivered); };
            var baseline = transport.HandledMessageCount;
            tags.Add(await channel.BasicConsumeAsync(queue, false, consumer));
            Assert.True(await seen.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            var failed = (await store.FindAsync(id))!;
            Assert.Equal(SagaStatus.Failed, failed.Status);
            Assert.Null(failed.CurrentStep);
            Assert.Equal(ExternalProcessStatus.Completed, failed.Steps[0].InvocationProgress!.Status);
            Assert.Contains(failed.Steps[0].ObservedMessages, m => m.MessageId == terminal.Message.MessageId);
            Assert.Null(failed.Steps[0].Result);
            Assert.NotNull(failed.Steps[0].Reconciliation);
            Assert.Contains("business Result is null", failed.FailureReason);
            Assert.Equal(SagaStepStatus.Pending, failed.Steps[1].Status);
            await AssertQueueCount(next, 0);
            Assert.Equal(baseline + 1, transport.HandledMessageCount);
            var failures = transport.RejectedMessageCount;
            await targetHost.Services.GetRequiredService<IProcessInvocationBus>().PublishAsync(terminal);
            await Wait(() => Task.FromResult(Volatile.Read(ref completedDeliveries) == 2));
            await Task.Delay(300);
            Assert.Equal(baseline + 2, transport.HandledMessageCount); // No automatic requeue loop.
            Assert.Equal(failures, transport.RejectedMessageCount); // Valid terminal was ACKed, not rejected/lost.
            Assert.Equal(0u, await probe.MessageCountAsync(queue));
            Assert.Equal(0u, await transport.GetDeferredCountAsync());
            var duplicate = (await store.FindAsync(id))!;
            Assert.Equal(failed.Steps[0].Reconciliation, duplicate.Steps[0].Reconciliation);
            Assert.Equal(SagaStatus.Failed, duplicate.Status);
            Assert.Equal(SagaStepStatus.Pending, duplicate.Steps[1].Status);
            await AssertQueueCount(next, 0);
        }
        finally { await sourceHost.StopAsync(); await targetHost.StopAsync(); }
    }
}
