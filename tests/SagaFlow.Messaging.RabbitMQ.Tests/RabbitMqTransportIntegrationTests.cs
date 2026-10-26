using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Messages;
using SagaFlow.Core.State;
using SagaFlow.Engine.Persistence.InMemory;
using SagaFlow.Hosting.DependencyInjection;
using SagaFlow.Messaging.RabbitMQ.DependencyInjection;

namespace SagaFlow.Messaging.RabbitMQ.Tests;

[Collection(RabbitMqIntegrationCollection.Name)]
public sealed class RabbitMqTransportIntegrationTests(RabbitMqBrokerFixture broker)
{
    [RabbitMqFact]
    public async Task ParameterlessRegistrationUsesHostConnectionAndExistingTopology()
    {
        var queue = $"sagaflow.tests.parameterless.{Guid.NewGuid():N}";
        await using (var channel = await broker.Connection.CreateChannelAsync())
        {
            await channel.ExchangeDeclareAsync("sagaflow.commands", ExchangeType.Direct, durable: true);
            await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false);
            await channel.QueueBindAsync(queue, "sagaflow.commands", "DoWork");
        }

        var definitionPath = await WriteDefinitionAsync();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(broker.Connection);
        builder.Services.AddSagaFlow(options => options.LoadFromYaml(definitionPath));
        builder.Services.AddSagaFlowRabbitMq();
        var host = builder.Build();

        try
        {
            await host.StartAsync();
            await StartSagaAsync(host, new { id = 1 });
            Assert.NotNull(await GetMessageAsync(queue));
            Assert.Same(
                broker.Connection,
                host.Services.GetRequiredService<RabbitMqConnectionProvider>().Connection);
        }
        finally
        {
            await host.StopAsync();
            if (host is IAsyncDisposable asyncHost) await asyncHost.DisposeAsync();
            else host.Dispose();
            File.Delete(definitionPath);
            await using var channel = await broker.Connection.CreateChannelAsync();
            await channel.QueueDeleteAsync(queue);
        }
    }

    [RabbitMqFact]
    public async Task CommandIsSerializedAsJsonAndPublishedToBroker()
    {
        await using var runtime = await StartExternalAsync();

        var sagaId = await StartSagaAsync(runtime.Host, new { orderId = 42 });
        var delivery = await GetMessageAsync(runtime.Topology.CommandQueue);
        var command = RabbitMqMessageSerializer.DeserializeCommand(delivery.Body.Span);

        Assert.Equal("application/json", delivery.BasicProperties.ContentType);
        Assert.Equal(nameof(SagaCommand), delivery.BasicProperties.Type);
        Assert.Equal("DoWork", command.Name);
        Assert.Equal(42, command.Payload.GetProperty("orderId").GetInt32());
        Assert.Equal(sagaId.ToString("N"), command.Headers.SagaInstanceId);
    }

    [RabbitMqFact]
    public async Task CommandNameRoutesOnlyToConfiguredParticipantQueue()
    {
        var topology = RabbitTopology.Create();
        var otherQueue = $"sagaflow.tests.other.{Guid.NewGuid():N}";
        await using var runtime = await StartExternalAsync(topology, options =>
            options.RouteCommand("OtherWork", otherQueue));

        await StartSagaAsync(runtime.Host, new { id = 1 });

        Assert.NotNull(await GetMessageAsync(topology.CommandQueue));
        await using var channel = await broker.Connection.CreateChannelAsync();
        Assert.Null(await channel.BasicGetAsync(otherQueue, autoAck: true));
        await channel.QueueDeleteAsync(otherQueue);
    }

    [RabbitMqFact]
    public async Task OutcomeRoundTripAdvancesSagaThroughExistingReceiver()
    {
        await using var runtime = await StartExternalAsync();
        var sagaId = await StartSagaAsync(runtime.Host, new { id = 1 });
        var delivery = await GetMessageAsync(runtime.Topology.CommandQueue);
        var command = RabbitMqMessageSerializer.DeserializeCommand(delivery.Body.Span);

        await PublishOutcomeAsync(runtime.Topology.OutcomeQueue, new()
        {
            Name = "WorkCompleted",
            IsSuccess = true,
            Headers = command.Headers,
            Result = System.Text.Json.JsonSerializer.SerializeToElement(new { accepted = true })
        });

        await WaitUntilAsync(async () => (await FindSagaAsync(runtime.Host, sagaId))?.Status == SagaStatus.Completed);
    }

    [RabbitMqFact]
    public async Task CorrelationAndStepHeadersSurviveRabbitMqRoundTrip()
    {
        await using var runtime = await StartExternalAsync();
        var sagaId = await StartSagaAsync(runtime.Host, new { id = 1 });
        var delivery = await GetMessageAsync(runtime.Topology.CommandQueue);
        var command = RabbitMqMessageSerializer.DeserializeCommand(delivery.Body.Span);

        Assert.Equal(sagaId.ToString("N"), command.Headers.SagaInstanceId);
        Assert.Equal("work", command.Headers.StepId);
        Assert.False(string.IsNullOrWhiteSpace(command.Headers.CorrelationId));
        Assert.Equal(command.Headers.CorrelationId, delivery.BasicProperties.CorrelationId);
        Assert.Equal(runtime.Topology.OutcomeQueue, delivery.BasicProperties.ReplyTo);
    }

    [RabbitMqFact]
    public async Task HostOwnedConnectionIsReused()
    {
        await using var runtime = await StartExternalAsync();

        var provider = runtime.Host.Services.GetRequiredService<RabbitMqConnectionProvider>();

        Assert.False(provider.OwnsConnection);
        Assert.Same(broker.Connection, provider.Connection);
    }

    [RabbitMqFact]
    public async Task HostOwnedConnectionIsNotClosedBySagaFlow()
    {
        var connection = await broker.CreateFactory("sagaflow-host-owned-test").CreateConnectionAsync();
        var runtime = await StartExternalAsync(connection: connection);

        await runtime.DisposeAsync();

        Assert.True(connection.IsOpen);
        await connection.CloseAsync();
        await connection.DisposeAsync();
    }

    [RabbitMqFact]
    public async Task ManagedConnectionIsCreatedBySagaFlow()
    {
        await using var runtime = await StartManagedAsync();

        var provider = runtime.Host.Services.GetRequiredService<RabbitMqConnectionProvider>();

        Assert.True(provider.OwnsConnection);
        Assert.NotNull(provider.Connection);
        Assert.True(provider.Connection.IsOpen);
    }

    [RabbitMqFact]
    public async Task ManagedConnectionIsClosedWhenHostStops()
    {
        var runtime = await StartManagedAsync();
        var provider = runtime.Host.Services.GetRequiredService<RabbitMqConnectionProvider>();
        var connection = provider.Connection!;

        await runtime.DisposeAsync();

        Assert.False(connection.IsOpen);
    }

    [RabbitMqFact]
    public async Task MalformedOutcomeIsDiscardedAndConsumerContinues()
    {
        await using var runtime = await StartExternalAsync();
        var sagaId = await StartSagaAsync(runtime.Host, new { id = 1 });
        var delivery = await GetMessageAsync(runtime.Topology.CommandQueue);
        var command = RabbitMqMessageSerializer.DeserializeCommand(delivery.Body.Span);

        await PublishRawAsync(runtime.Topology.OutcomeQueue, "{not-json");
        await WaitUntilAsync(async () => await MessageCountAsync(runtime.Topology.OutcomeQueue) == 0);

        await PublishOutcomeAsync(runtime.Topology.OutcomeQueue, new()
        {
            IsSuccess = true,
            Headers = command.Headers,
            Result = System.Text.Json.JsonSerializer.SerializeToElement(new { accepted = true })
        });
        await WaitUntilAsync(async () => (await FindSagaAsync(runtime.Host, sagaId))?.Status == SagaStatus.Completed);
    }

    [RabbitMqFact]
    public async Task ConsumerIsCancelledDuringHostShutdown()
    {
        await using var runtime = await StartExternalAsync();
        Assert.Equal(1u, await ConsumerCountAsync(runtime.Topology.OutcomeQueue));

        await runtime.StopAsync();

        Assert.Equal(0u, await ConsumerCountAsync(runtime.Topology.OutcomeQueue));
    }

    [RabbitMqFact]
    public async Task MultipleOutcomeMessagesAreProcessed()
    {
        await using var runtime = await StartExternalAsync();
        var instances = new List<Guid>();

        for (var index = 0; index < 5; index++)
        {
            instances.Add(await StartSagaAsync(runtime.Host, new { id = index }));
            var delivery = await GetMessageAsync(runtime.Topology.CommandQueue);
            var command = RabbitMqMessageSerializer.DeserializeCommand(delivery.Body.Span);
            await PublishOutcomeAsync(runtime.Topology.OutcomeQueue, new()
            {
                IsSuccess = true,
                Headers = command.Headers,
                Result = System.Text.Json.JsonSerializer.SerializeToElement(new { accepted = true })
            });
        }

        await WaitUntilAsync(async () =>
        {
            foreach (var id in instances)
                if ((await FindSagaAsync(runtime.Host, id))?.Status != SagaStatus.Completed) return false;
            return true;
        });
    }

    [RabbitMqFact]
    public async Task DuplicateOutcomeIsAcknowledgedWithoutASecondTransition()
    {
        await using var runtime = await StartExternalAsync();
        var sagaId = await StartSagaAsync(runtime.Host, new { id = 1 });
        var command = RabbitMqMessageSerializer.DeserializeCommand(
            (await GetMessageAsync(runtime.Topology.CommandQueue)).Body.Span);
        var outcome = SuccessfulOutcome(command.Headers);

        await PublishOutcomeAsync(runtime.Topology.OutcomeQueue, outcome);
        await WaitUntilAsync(async () => (await FindSagaAsync(runtime.Host, sagaId))?.Status == SagaStatus.Completed);
        var completed = await FindSagaAsync(runtime.Host, sagaId);

        await PublishOutcomeAsync(runtime.Topology.OutcomeQueue, outcome);
        await WaitForConsumerAsync(runtime.Topology.OutcomeQueue);

        var afterDuplicate = await FindSagaAsync(runtime.Host, sagaId);
        Assert.Equal(completed!.Revision, afterDuplicate!.Revision);
        Assert.Equal(SagaStatus.Completed, afterDuplicate.Status);
        Assert.Equal(0u, await MessageCountAsync(runtime.Topology.CommandQueue));
    }

    [RabbitMqFact]
    public async Task LateOutcomeAfterCommandPublishFailureIsRequeuedWithoutChangingTerminalSaga()
    {
        const string definition = """
            name: rabbit-publish-failure-test
            version: "1.0"
            domain: test
            steps:
              - name: work
                command: DoWork
                transitions:
                  - event: success
                    nextStep: unroutable
                  - event: failure
                    nextStep: compensation
              - name: unroutable
                command: NoParticipantRoute
                transitions:
                  - event: success
                    nextStep: completed
                  - event: failure
                    nextStep: compensation
            """;
        await using var runtime = await StartExternalAsync(definitionYaml: definition);
        var sagaId = await StartSagaAsync(runtime.Host, new { id = 1 });
        var firstCommand = RabbitMqMessageSerializer.DeserializeCommand(
            (await GetMessageAsync(runtime.Topology.CommandQueue)).Body.Span);

        await PublishOutcomeAsync(runtime.Topology.OutcomeQueue, SuccessfulOutcome(firstCommand.Headers));
        await WaitUntilAsync(async () => (await FindSagaAsync(runtime.Host, sagaId))?.Status == SagaStatus.Failed);
        await WaitForConsumerAsync(runtime.Topology.OutcomeQueue);
        var failed = await FindSagaAsync(runtime.Host, sagaId);
        var failedStep = failed!.Steps.Single(step => step.StepId == "unroutable");
        Assert.Equal(SagaStepStatus.Failed, failedStep.Status);
        Assert.Null(failedStep.Result);

        await PublishOutcomeAsync(runtime.Topology.OutcomeQueue, SuccessfulOutcome(new()
        {
            SagaInstanceId = failed.Id,
            CorrelationId = failed.CorrelationId,
            StepId = failedStep.StepId
        }));
        await Task.Delay(250);
        await runtime.StopAsync();

        Assert.Equal(1u, await MessageCountAsync(runtime.Topology.OutcomeQueue));
        var afterLateOutcome = await FindSagaAsync(runtime.Host, sagaId);
        Assert.Equal(failed.Revision, afterLateOutcome!.Revision);
        Assert.Equal(SagaStatus.Failed, afterLateOutcome.Status);
        Assert.Null(afterLateOutcome.CurrentStep);
        Assert.Null(afterLateOutcome.Steps.Single(step => step.StepId == "unroutable").Result);
    }

    [RabbitMqFact]
    public async Task InvalidCorrelationAndStepAreRejectedWithoutChangingSagaState()
    {
        await using var runtime = await StartExternalAsync();
        var sagaId = await StartSagaAsync(runtime.Host, new { id = 1 });
        var command = RabbitMqMessageSerializer.DeserializeCommand(
            (await GetMessageAsync(runtime.Topology.CommandQueue)).Body.Span);
        var before = await FindSagaAsync(runtime.Host, sagaId);

        await PublishOutcomeAsync(runtime.Topology.OutcomeQueue, SuccessfulOutcome(new()
        {
            SagaInstanceId = command.Headers.SagaInstanceId,
            CorrelationId = "invalid-correlation",
            StepId = command.Headers.StepId
        }));
        await WaitForConsumerAsync(runtime.Topology.OutcomeQueue);

        await PublishOutcomeAsync(runtime.Topology.OutcomeQueue, SuccessfulOutcome(new()
        {
            SagaInstanceId = command.Headers.SagaInstanceId,
            CorrelationId = command.Headers.CorrelationId,
            StepId = "unknown-step"
        }));
        await WaitForConsumerAsync(runtime.Topology.OutcomeQueue);

        var after = await FindSagaAsync(runtime.Host, sagaId);
        Assert.Equal(before!.Revision, after!.Revision);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.CurrentStep, after.CurrentStep);
    }

    [RabbitMqFact]
    public async Task ProcessingExceptionRequeuesOutcomeInsteadOfDroppingIt()
    {
        var store = new FaultInjectingStateStore();
        await using var runtime = await StartExternalAsync(store: store);
        var sagaId = await StartSagaAsync(runtime.Host, new { id = 1 });
        var command = RabbitMqMessageSerializer.DeserializeCommand(
            (await GetMessageAsync(runtime.Topology.CommandQueue)).Body.Span);
        store.FailNextUpdates(1);

        await PublishOutcomeAsync(runtime.Topology.OutcomeQueue, SuccessfulOutcome(command.Headers));

        await WaitUntilAsync(async () => (await FindSagaAsync(runtime.Host, sagaId))?.Status == SagaStatus.Completed);
        Assert.Equal(1, store.InjectedFailureCount);
    }

    [RabbitMqFact]
    public async Task RedeliveredOutcomeIsNotDiscardedAfterAnotherTransientFailure()
    {
        var store = new FaultInjectingStateStore();
        await using var runtime = await StartExternalAsync(store: store);
        var sagaId = await StartSagaAsync(runtime.Host, new { id = 1 });
        var command = RabbitMqMessageSerializer.DeserializeCommand(
            (await GetMessageAsync(runtime.Topology.CommandQueue)).Body.Span);
        store.FailNextUpdates(2);

        await PublishOutcomeAsync(runtime.Topology.OutcomeQueue, SuccessfulOutcome(command.Headers));

        await WaitUntilAsync(async () => (await FindSagaAsync(runtime.Host, sagaId))?.Status == SagaStatus.Completed);
        Assert.Equal(2, store.InjectedFailureCount);
    }

    [RabbitMqFact]
    public async Task MandatoryUnroutablePublishFailsExplicitly()
    {
        await using var runtime = await StartExternalAsync();
        var bus = runtime.Host.Services.GetRequiredService<ISagaMessageBus>();
        var command = CreateCommand("NoParticipantRoute", 1);

        var error = await Assert.ThrowsAnyAsync<PublishException>(
            () => bus.PublishAsync(command).AsTask());

        Assert.True(error.IsReturn);
    }

    [RabbitMqFact]
    public async Task ConcurrentCommandPublicationLosesOrCorruptsNoMessages()
    {
        const int messageCount = 100;
        await using var runtime = await StartExternalAsync();
        var bus = runtime.Host.Services.GetRequiredService<ISagaMessageBus>();
        var commands = Enumerable.Range(0, messageCount)
            .Select(index => CreateCommand("DoWork", index))
            .ToArray();

        await Task.WhenAll(commands.Select(command => bus.PublishAsync(command).AsTask()));

        var received = new List<SagaCommand>(messageCount);
        await WaitUntilAsync(async () =>
        {
            await using var channel = await broker.Connection.CreateChannelAsync();
            while (await channel.BasicGetAsync(runtime.Topology.CommandQueue, autoAck: true) is { } delivery)
                received.Add(RabbitMqMessageSerializer.DeserializeCommand(delivery.Body.Span));
            return received.Count == messageCount;
        });

        Assert.Equal(messageCount, received.Count);
        Assert.Equal(messageCount, received.Select(command => command.Headers.SagaInstanceId).Distinct().Count());
        Assert.Equal(
            Enumerable.Range(0, messageCount),
            received.Select(command => command.Payload.GetProperty("index").GetInt32()).Order());
    }

    private async Task<TestRuntime> StartExternalAsync(
        RabbitTopology? topology = null,
        Action<RabbitMqOptions>? configure = null,
        IConnection? connection = null,
        ISagaStateStore? store = null,
        string? definitionYaml = null)
    {
        topology ??= RabbitTopology.Create();
        var definitionPath = await WriteDefinitionAsync(definitionYaml);
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(connection ?? broker.Connection);
        if (store is not null) builder.Services.AddSingleton(store);
        builder.Services.AddSagaFlow(options => options.LoadFromYaml(definitionPath));
        builder.Services.AddSagaFlowRabbitMqUsingHostConnection(options =>
        {
            ConfigureTopology(options, topology);
            configure?.Invoke(options);
        });
        var host = builder.Build();
        await host.StartAsync();
        return new(host, topology, definitionPath, broker.Connection);
    }

    private async Task<TestRuntime> StartManagedAsync()
    {
        var topology = RabbitTopology.Create();
        var definitionPath = await WriteDefinitionAsync();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSagaFlow(options => options.LoadFromYaml(definitionPath));
        builder.Services.AddSagaFlowRabbitMq(options =>
        {
            options.HostName = broker.HostName;
            options.Port = broker.Port;
            options.ClientProvidedName = $"sagaflow-managed-test-{Guid.NewGuid():N}";
            ConfigureTopology(options, topology);
        });
        var host = builder.Build();
        await host.StartAsync();
        return new(host, topology, definitionPath, broker.Connection);
    }

    private static void ConfigureTopology(RabbitMqOptions options, RabbitTopology topology)
    {
        options.CommandExchange = topology.CommandExchange;
        options.OutcomeQueue = topology.OutcomeQueue;
        options.RouteCommand("DoWork", topology.CommandQueue);
    }

    private static async Task<Guid> StartSagaAsync(IHost host, object payload)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISagaFlow>().StartAsync(payload);
    }

    private static async Task<SagaInstance?> FindSagaAsync(IHost host, Guid id) =>
        await host.Services.GetRequiredService<ISagaStateStore>().FindAsync(id);

    private async Task<BasicGetResult> GetMessageAsync(string queue)
    {
        BasicGetResult? result = null;
        await WaitUntilAsync(async () =>
        {
            await using var channel = await broker.Connection.CreateChannelAsync();
            result = await channel.BasicGetAsync(queue, autoAck: true);
            return result is not null;
        });
        return result!;
    }

    private async Task PublishOutcomeAsync(string queue, SagaOutcomeEvent outcome) =>
        await PublishRawAsync(queue, RabbitMqMessageSerializer.Serialize(outcome));

    private static SagaOutcomeEvent SuccessfulOutcome(SagaMessageHeaders headers) => new()
    {
        Name = "WorkCompleted",
        IsSuccess = true,
        Headers = headers,
        Result = JsonSerializer.SerializeToElement(new { accepted = true })
    };

    private static SagaCommand CreateCommand(string name, int index) => new()
    {
        Name = name,
        Payload = JsonSerializer.SerializeToElement(new { index }),
        Headers = new()
        {
            SagaInstanceId = Guid.NewGuid().ToString("N"),
            CorrelationId = Guid.NewGuid().ToString("N"),
            StepId = "work"
        }
    };

    private async Task PublishRawAsync(string queue, string body) =>
        await PublishRawAsync(queue, Encoding.UTF8.GetBytes(body));

    private async Task PublishRawAsync(string queue, byte[] body)
    {
        await using var channel = await broker.Connection.CreateChannelAsync(new CreateChannelOptions(true, true));
        await channel.BasicPublishAsync(
            exchange: string.Empty,
            routingKey: queue,
            mandatory: true,
            basicProperties: new BasicProperties { ContentType = "application/json", DeliveryMode = DeliveryModes.Persistent },
            body);
    }

    private async Task<uint> MessageCountAsync(string queue)
    {
        await using var channel = await broker.Connection.CreateChannelAsync();
        return await channel.MessageCountAsync(queue);
    }

    private async Task<uint> ConsumerCountAsync(string queue)
    {
        await using var channel = await broker.Connection.CreateChannelAsync();
        return await channel.ConsumerCountAsync(queue);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < timeout)
        {
            if (await condition()) return;
            await Task.Delay(50);
        }
        Assert.Fail("RabbitMQ integration condition was not met within 10 seconds.");
    }

    private async Task WaitForConsumerAsync(string queue)
    {
        await WaitUntilAsync(async () => await MessageCountAsync(queue) == 0);
        await Task.Delay(200);
        Assert.Equal(0u, await MessageCountAsync(queue));
    }

    private static async Task<string> WriteDefinitionAsync(string? definitionYaml = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"sagaflow-rabbitmq-{Guid.NewGuid():N}.yaml");
        await File.WriteAllTextAsync(path, definitionYaml ?? """
            name: rabbit-test
            version: "1.0"
            domain: test
            steps:
              - name: work
                command: DoWork
                transitions:
                  - event: success
                    nextStep: completed
                  - event: failure
                    nextStep: compensation
            """);
        return path;
    }

    private sealed record RabbitTopology(string CommandExchange, string CommandQueue, string OutcomeQueue)
    {
        public static RabbitTopology Create()
        {
            var suffix = Guid.NewGuid().ToString("N");
            return new($"sagaflow.tests.commands.{suffix}", $"sagaflow.tests.worker.{suffix}", $"sagaflow.tests.outcomes.{suffix}");
        }
    }

    private sealed class FaultInjectingStateStore : ISagaStateStore
    {
        private readonly InMemorySagaStateStore _inner = new();
        private int _remainingFailures;
        private int _injectedFailureCount;

        public int InjectedFailureCount => Volatile.Read(ref _injectedFailureCount);

        public void FailNextUpdates(int count) => Interlocked.Exchange(ref _remainingFailures, count);

        public ValueTask SaveAsync(SagaInstance instance, CancellationToken cancellationToken = default) =>
            _inner.SaveAsync(instance, cancellationToken);

        public ValueTask<SagaInstance?> FindAsync(string instanceId, CancellationToken cancellationToken = default) =>
            _inner.FindAsync(instanceId, cancellationToken);

        public ValueTask<TResult> UpdateAsync<TResult>(
            string instanceId,
            Func<SagaInstance, TResult> update,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Decrement(ref _remainingFailures) >= 0)
            {
                Interlocked.Increment(ref _injectedFailureCount);
                throw new IOException("Injected transient state-store failure.");
            }

            return _inner.UpdateAsync(instanceId, update, cancellationToken);
        }
    }

    private sealed class TestRuntime(IHost host, RabbitTopology topology, string definitionPath, IConnection cleanupConnection) : IAsyncDisposable
    {
        private bool _disposed;
        public IHost Host { get; } = host;
        public RabbitTopology Topology { get; } = topology;

        public Task StopAsync() => Host.StopAsync();

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await Host.StopAsync();
            if (Host is IAsyncDisposable asyncHost) await asyncHost.DisposeAsync();
            else Host.Dispose();
            File.Delete(definitionPath);

            if (!cleanupConnection.IsOpen) return;
            await using var channel = await cleanupConnection.CreateChannelAsync();
            await channel.QueueDeleteAsync(Topology.CommandQueue);
            await channel.QueueDeleteAsync(Topology.OutcomeQueue);
            await channel.ExchangeDeleteAsync(Topology.CommandExchange);
        }
    }
}
