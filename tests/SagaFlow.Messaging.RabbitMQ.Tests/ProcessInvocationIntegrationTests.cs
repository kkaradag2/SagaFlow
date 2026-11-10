using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using SagaFlow.Hosting.Endpoints;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
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
using SagaFlow.Messaging.RabbitMQ.DependencyInjection;
using SagaFlow.Storage.PostgreSQL;
using SagaFlow.Storage.PostgreSQL.DependencyInjection;
using SagaFlow.Storage.PostgreSQL.Schema;
using SagaFlow.Storage.PostgreSQL.Tests;

namespace SagaFlow.Messaging.RabbitMQ.Tests;
[Collection(RabbitMqIntegrationCollection.Name)]
public sealed partial class ProcessInvocationIntegrationTests(RabbitMqBrokerFixture broker)
{
    [OutcomeRecoveryFact]
    public async Task ExplicitTerminalCommitSurvivesHostInterruptionBeforePublication()
    {
        await using var database = new PostgreSqlTestDatabase();
        await database.CreateAsync();
        await PostgreSqlStorageSchema.InitializeAsync(database.ConnectionString);
        var suffix = Guid.NewGuid().ToString("N");
        var definition = Definition("target" + suffix, "remote", "targetStep" + suffix);
        var identity = new ProcessInvocationIdentity(Guid.NewGuid().ToString("N"), "parent-correlation",
            new("parent" + suffix, "source", "1.0"), Guid.NewGuid().ToString("N"), "invoke",
            new(definition.Domain, definition.Name, definition.Version));
        var request = new ProcessInvocationRequest(identity, JsonSerializer.SerializeToElement(new { allow = true }));
        ProcessInvocationTerminalOutcome response;
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var published = new NpgsqlCommand("""
            SELECT published_at IS NOT NULL FROM sagaflow.outbox WHERE message_id = @id
            """, connection);
        using (var first = BuildHost(definition, null, false, database.ConnectionString))
        {
            await using var scope = first.Services.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var target = await services.GetRequiredService<ProcessInvocationManager>().GetOrCreateTargetAsync(request);
            using var interrupted = new CancellationTokenSource();
            var publisher = new ProcessInvocationPublisher(services.GetRequiredService<ISagaStateStore>(),
                services.GetRequiredService<IProcessInvocationBus>(), services.GetRequiredService<ProcessDefinitionRegistry>(),
                services.GetRequiredService<ISagaClock>(), services.GetRequiredService<CommandDispatcher>(),
                new SagaStateChangePublisher([new InterruptAfterCommit(interrupted)]));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publisher.TransitionTerminalAsync(
                target.Id, SagaStatus.Completed,
                state => state.Context = JsonSerializer.SerializeToElement(new { result = "explicit" }), interrupted.Token));

            var state = (await new PostgreSqlSagaStateStore(database.ConnectionString).FindAsync(target.Id))!;
            Assert.Equal(SagaStatus.Completed, state.Status);
            Assert.Equal("explicit", state.Context.GetProperty("result").GetString());
            response = Assert.IsType<ProcessInvocationTerminalOutcome>(state.InvocationTerminalOutcome);
            published.Parameters.AddWithValue("id", response.Message.MessageId);
            Assert.Equal(false, await published.ExecuteScalarAsync());
            await using var pending = new NpgsqlCommand("""
                SELECT owner IS NULL AND attempts = 0 AND published_at IS NULL
                FROM sagaflow.outbox WHERE message_id = @id
                """, connection);
            pending.Parameters.AddWithValue("id", response.Message.MessageId);
            Assert.Equal(true, await pending.ExecuteScalarAsync());
        }

        var queue = RabbitMqInvocationTransport.Queue(identity.Source, false);
        await using var channel = await broker.Connection.CreateChannelAsync(new CreateChannelOptions(true, true));
        await channel.ExchangeDeclareAsync(RabbitMqInvocationTransport.Exchange, ExchangeType.Direct, durable: true);
        await channel.QueueDeclareAsync(queue, true, false, false);
        await channel.QueueBindAsync(queue, RabbitMqInvocationTransport.Exchange, queue);
        Assert.Equal(0u, await channel.MessageCountAsync(queue));
        using var restarted = await Start(definition, postgreSql: database.ConnectionString);
        try
        {
            await Wait(async () => Equals(true, await published.ExecuteScalarAsync()));
            var delivered = Assert.IsType<BasicGetResult>(await channel.BasicGetAsync(queue, autoAck: true));
            Assert.Equal(response, RabbitMqInvocationSerializer.Read<ProcessInvocationTerminalOutcome>(
                delivered.Body.Span, delivered.BasicProperties));
            Assert.Equal(0u, await channel.MessageCountAsync(queue));
        }
        finally { await restarted.StopAsync(); }
    }

    private sealed class InterruptAfterCommit(CancellationTokenSource interruption) : ISagaStateChangeListener
    {
        public ValueTask OnStateChangedAsync(SagaStateChanged notification, CancellationToken cancellationToken)
        {
            interruption.Cancel();
            return ValueTask.CompletedTask;
        }
    }

    [OutcomeRecoveryFact]
    public async Task ConfirmedButUnmarkedRequestReplaysTheSameWireMessageAndCreatesOneTarget()
    {
        await using var parentDatabase = new PostgreSqlTestDatabase();
        await using var childDatabase = new PostgreSqlTestDatabase();
        await parentDatabase.CreateAsync();
        await childDatabase.CreateAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var target = Definition("target" + suffix, "remote", "targetStep" + suffix);
        var source = SourceDefinition(target, suffix);
        var store = new PostgreSqlSagaStateStore(parentDatabase.ConnectionString);
        Guid sourceId;
        using (var first = await Start(source, target.Name, postgreSql: parentDatabase.ConnectionString))
        {
            try
            {
                await using var scope = first.Services.CreateAsyncScope();
                sourceId = await scope.ServiceProvider.GetRequiredService<ISagaFlow>().StartAsync(new { allow = true });
            }
            finally { await first.StopAsync(); }
        }

        var request = (await ((ISagaStateStore)store).FindAsync(sourceId))!.Steps[0].Invocation!;
        var queue = RabbitMqInvocationTransport.Queue(request.Identity.Target, true);
        await using var channel = await broker.Connection.CreateChannelAsync(new CreateChannelOptions(true, true));
        await channel.QueueDeclareAsync(queue, true, false, false);
        await channel.QueueBindAsync(queue, RabbitMqInvocationTransport.Exchange, queue);
        IReadOnlyList<OutboxClaim> claims = [];
        await Wait(async () =>
        {
            claims = await store.ClaimOutboxAsync("interrupted-confirm", DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(1), 2);
            return claims.Count != 0;
        });
        var stored = Assert.Single(claims).Message;
        Assert.Equal(request.Message.MessageId, stored.MessageId);
        await channel.BasicPublishAsync(stored.Exchange!, stored.Destination, true,
            RabbitMqInvocationSerializer.Properties((ProcessInvocationRequest)stored.ToInvocation(), request.Message), stored.Body);
        // The real broker confirmed, but this claim deliberately never marks the row Published.

        using var restarted = await Start(source, target.Name, postgreSql: parentDatabase.ConnectionString);
        try
        {
            await Wait(async () => await channel.MessageCountAsync(queue) == 2);
            var first = Assert.IsType<BasicGetResult>(await channel.BasicGetAsync(queue, autoAck: false));
            var replay = Assert.IsType<BasicGetResult>(await channel.BasicGetAsync(queue, autoAck: false));
            Assert.Equal(stored.Body, first.Body.ToArray());
            Assert.Equal(first.Body.ToArray(), replay.Body.ToArray());
            Assert.Equal(request.Message, RabbitMqInvocationSerializer.Read<ProcessInvocationRequest>(replay.Body.Span, replay.BasicProperties).Message);
            Assert.Equal(first.BasicProperties.ReplyTo, replay.BasicProperties.ReplyTo);
            Assert.Equal(first.BasicProperties.CorrelationId, replay.BasicProperties.CorrelationId);
            foreach (var name in first.BasicProperties.Headers!.Keys.Where(key => key.StartsWith("sf-", StringComparison.Ordinal)))
                Assert.Equal(Assert.IsType<byte[]>(first.BasicProperties.Headers[name]), Assert.IsType<byte[]>(replay.BasicProperties.Headers![name]));
            await channel.BasicNackAsync(replay.DeliveryTag, multiple: true, requeue: true);
            using var child = await Start(target, postgreSql: childDatabase.ConnectionString);
            try
            {
                var receiver = child.Services.GetRequiredService<RabbitMqInvocationTransport>();
                await Wait(async () => receiver.HandledMessageCount == 2 &&
                    (await ((ISagaStateStore)store).FindAsync(sourceId))!.Steps[0].InvocationProgress!.Status == ExternalProcessStatus.Accepted);
                var state = Assert.Single(await child.Services.GetRequiredService<ISagaStateStore>().ListAsync());
                Assert.Equal(request.Message, state.IncomingInvocation!.RequestMessage);
                Assert.Equal(state.Id, (await ((ISagaStateStore)store).FindAsync(sourceId))!.Steps[0].InvocationProgress!.TargetSagaInstanceId);
            }
            finally { await child.StopAsync(); }
        }
        finally { await restarted.StopAsync(); }
    }

    [RabbitMqFact]
    public async Task IndependentHostsExchangeRequestsAcceptanceRejectionAndTerminalOutcomes()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var target = Definition("target" + suffix, "remote", "targetStep" + suffix);
        var source = new SagaDefinition
        {
            Domain = "source" + suffix, Name = "local", Version = "1.0",
            Steps = Definition("source", "local", "sourceStep" + suffix).Steps,
            BigPicture = [new() { Domain = target.Domain, Process = target.Name, Version = target.Version,
                Endpoint = "https://not-a-message-route", Relationship = "invokes" }]
        };
        using var targetHost = await Start(target);
        using var sourceHost = await Start(source, target.Name);
        try
        {
            var sourceStore = sourceHost.Services.GetRequiredService<ISagaStateStore>();
            var targetStore = targetHost.Services.GetRequiredService<ISagaStateStore>();
            string acceptedId;
            await using (var scope = sourceHost.Services.CreateAsyncScope())
                acceptedId = (await scope.ServiceProvider.GetRequiredService<ISagaFlow>().StartAsync(new { allow = true, value = 42 })).ToString("N");
            await Wait(async () => (await sourceStore.FindAsync(acceptedId))!.Steps[0].InvocationProgress!.Status == ExternalProcessStatus.Accepted);
            var sourceState = (await sourceStore.FindAsync(acceptedId))!;
            var request = sourceState.Steps[0].Invocation!;
            var bus = sourceHost.Services.GetRequiredService<IProcessInvocationBus>();
            await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => bus.PublishAsync(request).AsTask()));
            await Wait(async () => (await targetStore.ListAsync()).Count == 1);
            var targetState = Assert.Single(await targetStore.ListAsync());
            Assert.NotEqual(sourceState.Id, targetState.Id);
            Assert.NotEqual(sourceState.CorrelationId, targetState.CorrelationId);
            Assert.Equal(request.Identity, targetState.IncomingInvocation!.Identity);
            Assert.Equal(targetState.Id, sourceState.Steps[0].InvocationProgress!.TargetSagaInstanceId);
            Assert.All(targetState.Steps, step => Assert.Equal(SagaStepStatus.Dispatched, step.Status));
            Assert.Equal(42, targetState.Context.GetProperty("value").GetInt32());
            await using (var scope = targetHost.Services.CreateAsyncScope())
            {
                var publisher = scope.ServiceProvider.GetRequiredService<ProcessInvocationPublisher>();
                await publisher.TransitionTerminalAsync(targetState.Id, SagaStatus.Completed);
                await publisher.TransitionTerminalAsync(targetState.Id, SagaStatus.Completed);
            }
            await Wait(async () => (await sourceStore.FindAsync(acceptedId))!.Steps[0].InvocationProgress!.Status == ExternalProcessStatus.Completed);
            Assert.Equal(SagaStatus.Running, (await sourceStore.FindAsync(acceptedId))!.Status);
            string rejectedId;
            await using (var scope = sourceHost.Services.CreateAsyncScope())
                rejectedId = (await scope.ServiceProvider.GetRequiredService<ISagaFlow>().StartAsync(new { allow = false })).ToString("N");
            await Wait(async () => (await sourceStore.FindAsync(rejectedId))!.Steps[0].InvocationProgress!.Status == ExternalProcessStatus.Rejected);
            var rejected = (await sourceStore.FindAsync(rejectedId))!.Steps[0].Invocation!;
            await bus.PublishAsync(rejected);
            await Wait(async () => await targetHost.Services.GetRequiredService<ISagaStateStore>().FindIncomingDecisionAsync(rejected.Identity.InvocationId) is not null);
            Assert.Single(await targetStore.ListAsync());
            Assert.Null((await sourceStore.FindAsync(rejectedId))!.Steps[0].InvocationProgress!.TargetSagaInstanceId);
            await Wait(async () =>
            {
                await using var channel = await broker.Connection.CreateChannelAsync();
                return await channel.MessageCountAsync(RabbitMqInvocationTransport.Queue(new(target.Domain, target.Name, target.Version), true)) == 0;
            });
        }
        finally { await sourceHost.StopAsync(); await targetHost.StopAsync(); }
    }

    [RabbitMqFact]
    public async Task UnroutableRequestRetainsRequestedIdentityAndCanBeRetriedWhenTargetStarts()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var target = Definition("target" + suffix, "remote", "targetStep" + suffix);
        var source = new SagaDefinition { Domain = "source" + suffix, Name = "local", Version = "1.0",
            Steps = Definition("source", "local", "sourceStep" + suffix).Steps,
            BigPicture = [new() { Domain = target.Domain, Process = target.Name, Version = target.Version, Endpoint = "https://discovery", Relationship = "invokes" }] };
        using var sourceHost = await Start(source, target.Name);
        try
        {
            Guid id;
            await using (var scope = sourceHost.Services.CreateAsyncScope())
                id = await scope.ServiceProvider.GetRequiredService<ISagaFlow>().StartAsync(new { allow = true });
            var store = sourceHost.Services.GetRequiredService<ISagaStateStore>();
            var before = (await store.FindAsync(id))!;
            Assert.Equal(SagaStatus.Running, before.Status);
            Assert.Equal(ExternalProcessStatus.Requested, before.Steps[0].InvocationProgress!.Status);
            Assert.True(before.Steps[0].Publication!.Queued);
            var pending = await HoldRequestAsync(store, before);
            Assert.Equal(before.Steps[0].Invocation!.Message.MessageId, pending.Message.MessageId);
            using var targetHost = await Start(target);
            try
            {
                await store.ReleaseOutboxAsync(pending.Message.MessageId, pending.Owner, DateTimeOffset.UtcNow);
                await Wait(async () => (await store.FindAsync(id))!.Steps[0].InvocationProgress!.Status == ExternalProcessStatus.Accepted);
                Assert.Equal(before.Steps[0].Invocation!.Message, (await store.FindAsync(id))!.Steps[0].Invocation!.Message);
                Assert.Single(await targetHost.Services.GetRequiredService<ISagaStateStore>().ListAsync());
            }
            finally { await targetHost.StopAsync(); }
        }
        finally { await sourceHost.StopAsync(); }
    }
    [RabbitMqFact]
    public async Task FailedLocalHandlerNeverPublishesItsPreparedRequest()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var target = Definition("target" + suffix, "remote", "targetStep" + suffix);
        var source = new SagaDefinition { Domain = "source" + suffix, Name = "local", Version = "1.0",
            Steps = Definition("source", "local", "sourceStep" + suffix).Steps,
            BigPicture = [new() { Domain = target.Domain, Process = target.Name, Version = target.Version, Endpoint = "https://discovery", Relationship = "invokes" }] };
        using var targetHost = await Start(target);
        using var sourceHost = await Start(source, target.Name, failHandler: true);
        try
        {
            await using var scope = sourceHost.Services.CreateAsyncScope();
            await Assert.ThrowsAsync<SagaDispatchException>(() => scope.ServiceProvider.GetRequiredService<ISagaFlow>().StartAsync(new { allow = true }));
            var state = Assert.Single(await sourceHost.Services.GetRequiredService<ISagaStateStore>().ListAsync());
            Assert.Equal(SagaStatus.Failed, state.Status);
            Assert.Equal(SagaStepStatus.Failed, state.Steps[0].Status);
            Assert.Equal(ExternalProcessStatus.Prepared, state.Steps[0].InvocationProgress!.Status);
            Assert.Null(state.Steps[0].Publication);
            Assert.Empty(await targetHost.Services.GetRequiredService<ISagaStateStore>().ListAsync());
        }
        finally { await sourceHost.StopAsync(); await targetHost.StopAsync(); }
    }

    [RabbitMqFact]
    public async Task ParticipantPublicationFailureAfterRemoteRequestRetainsPendingCommand()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var target = Definition("target" + suffix, "remote", "targetStep" + suffix);
        var source = SourceDefinition(target, suffix);
        using var targetHost = await Start(target);
        using var sourceHost = await Start(source, target.Name);
        try
        {
            // Only the participant publisher channel is closed: the real invocation RabbitMQ channel stays available.
            await sourceHost.Services.GetRequiredService<RabbitMqTransport>().PublisherChannel!.CloseAsync(CancellationToken.None);
            await using var scope = sourceHost.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ISagaFlow>().StartAsync(new { allow = true });
            var sourceStore = sourceHost.Services.GetRequiredService<ISagaStateStore>();
            var initial = Assert.Single(await sourceStore.ListAsync());
            await Wait(async () => (await sourceStore.FindAsync(initial.Id))!.Steps[0].InvocationProgress!.Status == ExternalProcessStatus.Accepted);
            var state = (await sourceStore.FindAsync(initial.Id))!;
            var request = state.Steps[0].Invocation!;
            Assert.Equal(SagaStatus.Running, state.Status);
            Assert.Equal(SagaStepStatus.Dispatched, state.Steps[0].Status);
            Assert.Null(state.Steps[0].Reconciliation);
            var pending = Assert.Single(await sourceStore.ClaimOutboxAsync("inspection", DateTimeOffset.UtcNow.AddMinutes(5), TimeSpan.FromMinutes(1), 1));
            Assert.Equal(state.Steps[0].Execution!.CommandId, pending.Message.MessageId);
            var relationship = await scope.ServiceProvider.GetRequiredService<ProcessInvocationManager>().FindAsync(state.Id, request.Identity.InvocationId);
            Assert.Null(relationship.Reconciliation);
            var targetStore = targetHost.Services.GetRequiredService<ISagaStateStore>();
            await Wait(async () => (await targetStore.ListAsync()).SingleOrDefault()?.Steps[0].Status == SagaStepStatus.Dispatched);
            var before = Assert.Single(await targetStore.ListAsync());
            Assert.Equal(SagaStatus.Running, before.Status);
            Assert.All(before.Steps, step => Assert.Equal(SagaStepStatus.Dispatched, step.Status));
            var receiver = targetHost.Services.GetRequiredService<RabbitMqInvocationTransport>();
            var handled = receiver.HandledMessageCount;
            await sourceHost.Services.GetRequiredService<IProcessInvocationBus>().PublishAsync(request); // broker duplicate simulation, not a new authorized source attempt
            await Wait(() => Task.FromResult(receiver.HandledMessageCount > handled));
            Assert.Equal(before.Id, Assert.Single(await targetStore.ListAsync()).Id);
            Assert.Null((await sourceStore.FindAsync(state.Id))!.Steps[0].Reconciliation);
            Assert.Null((await targetStore.FindAsync(before.Id))!.InvocationTerminalOutcome);
        }
        finally { await sourceHost.StopAsync(); await targetHost.StopAsync(); }
    }

    [RabbitMqFact]
    public Task EarlyTerminalIsParkedAndAppliedAfterLaterAcceptance() => EarlyTerminal(true);

    [RabbitMqFact]
    public Task MissingAcceptanceLeavesObservableParkingWithoutAnActiveRedeliveryLoop() => EarlyTerminal(false);

    [RabbitMqFact]
    public async Task ParkingPublishFailureRetainsOriginalUntilConfirmedTransfer()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var target = Definition("target" + suffix, "remote", "targetStep" + suffix);
        var source = SourceDefinition(target, suffix);
        using var sourceHost = await Start(source, target.Name);
        IHost? targetHost = null;
        var deferred = RabbitMqInvocationTransport.Queue(new(source.Domain, source.Name, source.Version), false) + ".deferred";
        try
        {
            var pendingSource = await StartWithHeldRequestAsync(sourceHost);
            var request = pendingSource.Steps[0].Invocation!;
            targetHost = await Start(target);
            await using var targetScope = targetHost.Services.CreateAsyncScope();
            var targetState = await targetScope.ServiceProvider.GetRequiredService<ProcessInvocationManager>().GetOrCreateTargetAsync(request);
            var transport = sourceHost.Services.GetRequiredService<RabbitMqInvocationTransport>();
            await using (var channel = await broker.Connection.CreateChannelAsync())
                await channel.QueueDeleteAsync(deferred);
            await targetScope.ServiceProvider.GetRequiredService<ProcessInvocationPublisher>().TransitionTerminalAsync(targetState.Id, SagaStatus.Completed);
            await Wait(() => Task.FromResult(transport.ParkingFailureCount > 0));
            Assert.Equal(ExternalProcessStatus.Requested, (await sourceHost.Services.GetRequiredService<ISagaStateStore>().FindAsync(pendingSource.Id))!.Steps[0].InvocationProgress!.Status);
            await using (var channel = await broker.Connection.CreateChannelAsync())
                await channel.QueueDeclareAsync(deferred, durable: true, exclusive: false, autoDelete: false);
            var reconciliation = sourceHost.Services.GetRequiredService<IProcessInvocationReconciliation>();
            await Wait(async () => await reconciliation.GetDeferredCountAsync() == 1);
            var parked = transport.HandledMessageCount;
            await Task.Delay(500);
            Assert.Equal(parked, transport.HandledMessageCount);
            await targetHost.Services.GetRequiredService<IProcessInvocationBus>().PublishAsync(new ProcessInvocationAccepted(
                new(request.Identity, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow), targetState.Id));
            await Wait(async () => (await sourceHost.Services.GetRequiredService<ISagaStateStore>().FindAsync(pendingSource.Id))!.Steps[0].InvocationProgress!.Status == ExternalProcessStatus.Completed
                && await reconciliation.GetDeferredCountAsync() == 0);
            Assert.Equal(targetState.Id, Assert.Single(await targetHost.Services.GetRequiredService<ISagaStateStore>().ListAsync()).Id);
        }
        finally { await sourceHost.StopAsync(); if (targetHost is not null) { await targetHost.StopAsync(); targetHost.Dispose(); } }
    }

    [RabbitMqFact]
    public async Task ParkedTerminalsReplayOnlyTheirOwnInvocation()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var target = Definition("target" + suffix, "remote", "targetStep" + suffix);
        var source = SourceDefinition(target, suffix);
        using var sourceHost = await Start(source, target.Name);
        IHost? targetHost = null;
        try
        {
            var sourceStore = sourceHost.Services.GetRequiredService<ISagaStateStore>();
            var requests = new List<ProcessInvocationRequest>();
            for (var i = 0; i < 2; i++)
            {
                var pendingSource = await StartWithHeldRequestAsync(sourceHost, i);
                requests.Add(pendingSource.Steps[0].Invocation!);
            }
            Assert.NotEqual(requests[0].Identity.InvocationId, requests[1].Identity.InvocationId);
            Assert.NotEqual(requests[0].Message.MessageId, requests[1].Message.MessageId);
            targetHost = await Start(target);
            var targetStore = targetHost.Services.GetRequiredService<ISagaStateStore>();
            var targets = new List<SagaInstance>();
            await using (var scope = targetHost.Services.CreateAsyncScope())
            {
                var manager = scope.ServiceProvider.GetRequiredService<ProcessInvocationManager>();
                var publisher = scope.ServiceProvider.GetRequiredService<ProcessInvocationPublisher>();
                foreach (var request in requests)
                {
                    var state = await manager.GetOrCreateTargetAsync(request);
                    targets.Add(state);
                    await publisher.TransitionTerminalAsync(state.Id, SagaStatus.Completed);
                }
            }
            var reconciliation = sourceHost.Services.GetRequiredService<IProcessInvocationReconciliation>();
            await Wait(async () => await reconciliation.GetDeferredCountAsync() == 2);
            var bus = targetHost.Services.GetRequiredService<IProcessInvocationBus>();
            await bus.PublishAsync(new ProcessInvocationAccepted(new(requests[0].Identity, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow), targets[0].Id));
            await Wait(async () => (await sourceStore.FindAsync(requests[0].Identity.SourceSagaInstanceId))!.Steps[0].InvocationProgress!.Status == ExternalProcessStatus.Completed);
            Assert.Equal(ExternalProcessStatus.Requested, (await sourceStore.FindAsync(requests[1].Identity.SourceSagaInstanceId))!.Steps[0].InvocationProgress!.Status);
            Assert.Equal(1u, await reconciliation.GetDeferredCountAsync());
            await bus.PublishAsync(new ProcessInvocationAccepted(new(requests[1].Identity, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow), targets[1].Id));
            await Wait(async () => (await sourceStore.FindAsync(requests[1].Identity.SourceSagaInstanceId))!.Steps[0].InvocationProgress!.Status == ExternalProcessStatus.Completed
                && await reconciliation.GetDeferredCountAsync() == 0);
            var first = (await sourceStore.FindAsync(requests[0].Identity.SourceSagaInstanceId))!;
            var revision = first.Revision;
            var completedAt = first.Steps[0].InvocationProgress!.UpdatedAt;
            var terminal = (await targetStore.FindAsync(targets[0].Id))!.InvocationTerminalOutcome!;
            await bus.PublishAsync(terminal);
            await Wait(async () => (await sourceStore.FindAsync(first.Id))!.Revision > revision);
            Assert.Equal(ExternalProcessStatus.Completed, (await sourceStore.FindAsync(first.Id))!.Steps[0].InvocationProgress!.Status);
            Assert.Equal(completedAt, (await sourceStore.FindAsync(first.Id))!.Steps[0].InvocationProgress!.UpdatedAt);
            Assert.Equal(ExternalProcessStatus.Completed, (await sourceStore.FindAsync(requests[1].Identity.SourceSagaInstanceId))!.Steps[0].InvocationProgress!.Status);
            Assert.Equal(0u, await reconciliation.GetDeferredCountAsync());
        }
        finally { await sourceHost.StopAsync(); if (targetHost is not null) { await targetHost.StopAsync(); targetHost.Dispose(); } }
    }

    private async Task EarlyTerminal(bool acceptLater, bool external = false)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var target = Definition("target" + suffix, "remote", "targetStep" + suffix);
        var source = SourceDefinition(target, suffix);
        if (external) source = new SagaDefinition { Domain = source.Domain, Name = source.Name, Version = source.Version, BigPicture = source.BigPicture,
            Steps = [new() { Name = source.Steps[0].Name, Command = "InvokeRemote", External = true,
                Transitions = [new() { Event = "success", NextStep = "completed" }, new() { Event = "failure", NextStep = "compensation" }] }] };
        using var sourceHost = await Start(source, target.Name);
        IHost? targetHost = null;
        try
        {
            var sourceStore = sourceHost.Services.GetRequiredService<ISagaStateStore>();
            var state = await StartWithHeldRequestAsync(sourceHost);
            var request = state.Steps[0].Invocation!;
            targetHost = await Start(target);
            await using var targetScope = targetHost.Services.CreateAsyncScope();
            var targetState = await targetScope.ServiceProvider.GetRequiredService<ProcessInvocationManager>().GetOrCreateTargetAsync(request);
            var targetStore = targetHost.Services.GetRequiredService<ISagaStateStore>();
            await targetScope.ServiceProvider.GetRequiredService<ProcessInvocationPublisher>().TransitionTerminalAsync(targetState.Id, SagaStatus.Completed);
            var reconciliation = sourceHost.Services.GetRequiredService<IProcessInvocationReconciliation>();
            var transport = sourceHost.Services.GetRequiredService<RabbitMqInvocationTransport>();
            await Wait(async () => await reconciliation.GetDeferredCountAsync() == 1 && transport.HandledMessageCount >= 1);
            Assert.Equal(ExternalProcessStatus.Requested, (await sourceStore.FindAsync(state.Id))!.Steps[0].InvocationProgress!.Status);
            var storedTerminal = (await targetStore.FindAsync(targetState.Id))!.InvocationTerminalOutcome!;
            await targetHost.Services.GetRequiredService<IProcessInvocationBus>().PublishAsync(storedTerminal);
            await Wait(async () => await reconciliation.GetDeferredCountAsync() == 2 && transport.HandledMessageCount >= 2);
            var count = transport.HandledMessageCount;
            var replayCount = transport.DeferredReplayCount;
            Assert.Equal(0, await reconciliation.ReconcileDeferredAsync(1));
            Assert.Equal(replayCount + 1, transport.DeferredReplayCount);
            Assert.Equal(2u, await reconciliation.GetDeferredCountAsync());
            Assert.Equal(count, transport.HandledMessageCount); // explicit bounded replay did not redeliver through active consumer
            await using (var channel = await broker.Connection.CreateChannelAsync())
                Assert.Equal(0u, await channel.ConsumerCountAsync(RabbitMqInvocationTransport.Queue(new(source.Domain, source.Name, source.Version), false) + ".deferred"));
            if (acceptLater)
            {
                await targetHost.Services.GetRequiredService<IProcessInvocationBus>().PublishAsync(new ProcessInvocationAccepted(
                    new(request.Identity, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow), targetState.Id));
                await Wait(async () => (await sourceStore.FindAsync(state.Id))!.Steps[0].InvocationProgress!.Status == ExternalProcessStatus.Completed
                    && await reconciliation.GetDeferredCountAsync() == 0);
                var rejectedCount = transport.RejectedMessageCount;
                // A conflicting terminal uses the same stored message ID and is rejected by fingerprint/status validation.
                var terminal = (await targetStore.FindAsync(targetState.Id))!.InvocationTerminalOutcome!;
                await targetHost.Services.GetRequiredService<IProcessInvocationBus>().PublishAsync(new ProcessInvocationTerminalOutcome(terminal.Message, targetState.Id, ExternalProcessStatus.Failed));
                await Wait(() => Task.FromResult(transport.RejectedMessageCount > rejectedCount));
                Assert.Equal(ExternalProcessStatus.Completed, (await sourceStore.FindAsync(state.Id))!.Steps[0].InvocationProgress!.Status);
                Assert.Equal(0u, await reconciliation.GetDeferredCountAsync());
                if (external) Assert.Equal(SagaStatus.Completed, (await sourceStore.FindAsync(state.Id))!.Status);
            }
            else
            {
                replayCount = transport.DeferredReplayCount;
                Assert.Equal(0, await reconciliation.ReconcileDeferredAsync(64));
                Assert.Equal(replayCount + 2, transport.DeferredReplayCount);
                Assert.Equal(2u, await reconciliation.GetDeferredCountAsync());
                Assert.Equal(count, transport.HandledMessageCount);
            }
        }
        finally { await sourceHost.StopAsync(); if (targetHost is not null) { await targetHost.StopAsync(); targetHost.Dispose(); } }
        if (!acceptLater)
        {
            await using var channel = await broker.Connection.CreateChannelAsync();
            Assert.Equal(2u, await channel.MessageCountAsync(RabbitMqInvocationTransport.Queue(new(source.Domain, source.Name, source.Version), false) + ".deferred"));
        }
    }
    [RabbitMqFact]
    public Task ParkingReplayContinuesExternalStepAfterLateAcceptance() => EarlyTerminal(true, external: true);

    [RabbitMqFact]
    public Task TwoHostLocalCoordinationAutomaticallyCompletesAndContinuesSource() => RunCoordinated(ExternalProcessStatus.Completed);
    [RabbitMqFact]
    public Task TwoHostCompensationAutomaticallyContinuesSourceFailurePolicy() => RunCoordinated(ExternalProcessStatus.Compensated);
    [RabbitMqFact]
    public Task TwoHostCompensationFailureAutomaticallyNotifiesFailed() => RunCoordinated(ExternalProcessStatus.Failed);
    [RabbitMqFact]
    public Task InitialTargetDispatchFailureKeepsPendingCommandAndDuplicateRequestDoesNotRestart() => RunCoordinated(ExternalProcessStatus.Failed, true);

    private async Task RunCoordinated(ExternalProcessStatus expected, bool initialFailure = false)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var one = "targetOne" + suffix; var two = "targetTwo" + suffix;
        var invocationStep = "invoke" + suffix; var success = "success" + suffix; var failure = "failure" + suffix;
        var target = new SagaDefinition { Domain = "target" + suffix, Name = "remote", Version = "1.0",
            Steps = [new() { Name = one, Command = "Work", Compensation = new() { Command = "Undo" },
                    Transitions = [new() { Event = "success", NextStep = two }, new() { Event = "failure", NextStep = "compensation" }] },
                new() { Name = two, Command = "Work2", Transitions = [new() { Event = "success", NextStep = "completed" }, new() { Event = "failure", NextStep = "compensation" }] }] };
        var source = new SagaDefinition { Domain = "source" + suffix, Name = "local", Version = "1.0",
            BigPicture = [new() { Domain = target.Domain, Process = target.Name, Version = target.Version, Endpoint = "https://discovery", Relationship = "invokes" }],
            Steps = [new() { Name = invocationStep, Command = "InvokeRemote", External = true,
                    Transitions = [new() { Event = "success", NextStep = success }, new() { Event = "failure", NextStep = failure }] },
                new() { Name = success, Command = "Continue", Transitions = [new() { Event = "success", NextStep = "completed" }] },
                new() { Name = failure, Command = "Recover", Transitions = [new() { Event = "success", NextStep = "completed" }] }] };
        using var targetHost = await Start(target);
        using var sourceHost = await Start(source, target.Name);
        try
        {
            if (initialFailure) await targetHost.Services.GetRequiredService<RabbitMqTransport>().PublisherChannel!.CloseAsync(CancellationToken.None);
            string sourceId;
            await using (var scope = sourceHost.Services.CreateAsyncScope())
                sourceId = (await scope.ServiceProvider.GetRequiredService<ISagaFlow>().StartAsync(new { allow = true, value = 42 })).ToString("N");
            var sourceStore = sourceHost.Services.GetRequiredService<ISagaStateStore>();
            var targetStore = targetHost.Services.GetRequiredService<ISagaStateStore>();
            var targetTransport = targetHost.Services.GetRequiredService<RabbitMqInvocationTransport>();
            await Wait(() => Task.FromResult(targetTransport.HandledMessageCount >= 1));
            var targetState = Assert.Single(await targetStore.ListAsync());
            var request = (await sourceStore.FindAsync(sourceId))!.Steps[0].Invocation!;
            var handled = targetTransport.HandledMessageCount;
            await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => sourceHost.Services.GetRequiredService<IProcessInvocationBus>().PublishAsync(request).AsTask()));
            await Wait(() => Task.FromResult(targetTransport.HandledMessageCount >= handled + 5));
            var afterDuplicates = Assert.Single(await targetStore.ListAsync());
            Assert.Equal(targetState.Id, afterDuplicates.Id);
            if (initialFailure)
            {
                Assert.Equal(SagaStatus.Running, afterDuplicates.Status);
                Assert.Equal(SagaStepStatus.Dispatched, afterDuplicates.Steps[0].Status);
                Assert.Equal(targetState.Steps[0].Execution, afterDuplicates.Steps[0].Execution);
                await Wait(async () => (await sourceStore.FindAsync(sourceId))!.Steps[0].InvocationProgress?.Status ==
                    ExternalProcessStatus.Accepted);
                await AssertQueueCount(one, 0);
                return;
            }
            if (!initialFailure)
            {
                var first = await ReadParticipant(one);
                Assert.Equal(targetState.Id, first.Headers.SagaInstanceId);
                Assert.NotEqual(sourceId, first.Headers.SagaInstanceId);
                Assert.NotEqual(request.Identity.CorrelationId, first.Headers.CorrelationId);
                await AssertQueueCount(one, 0);
                await SendParticipantOutcome(target.Domain, first, true);
                var second = await ReadParticipant(two);
                await SendParticipantOutcome(target.Domain, second, expected == ExternalProcessStatus.Completed);
                if (expected != ExternalProcessStatus.Completed)
                {
                    var compensation = await ReadParticipant(one);
                    Assert.True(compensation.Headers.IsCompensation); Assert.Equal("Undo", compensation.Name);
                    await SendParticipantOutcome(target.Domain, compensation, expected == ExternalProcessStatus.Compensated);
                }
            }
            var nextStep = expected == ExternalProcessStatus.Completed ? success : failure;
            await Wait(async () =>
            {
                var state = (await sourceStore.FindAsync(sourceId))!;
                return state.CurrentStep == nextStep && state.Steps[0].InvocationProgress!.Status == expected;
            });
            var continued = await ReadParticipant(nextStep);
            Assert.Equal(sourceId, continued.Headers.SagaInstanceId);
            Assert.Equal(expected == ExternalProcessStatus.Completed ? "Continue" : "Recover", continued.Name);
            var finalTarget = (await targetStore.FindAsync(targetState.Id))!;
            Assert.Equal(expected.ToString(), finalTarget.Status.ToString());
            Assert.NotNull(finalTarget.InvocationTerminalOutcome);
            var sourceTransport = sourceHost.Services.GetRequiredService<RabbitMqInvocationTransport>();
            handled = sourceTransport.HandledMessageCount;
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => targetHost.Services.GetRequiredService<IProcessInvocationBus>().PublishAsync(finalTarget.InvocationTerminalOutcome!).AsTask()));
            await Wait(() => Task.FromResult(sourceTransport.HandledMessageCount >= handled + 4));
            await AssertQueueCount(nextStep, 0); // source continuation has a single participant dispatch
            if (initialFailure) await AssertQueueCount(one, 0);
            await SendParticipantOutcome(source.Domain, continued, true);
            await Wait(async () => (await sourceStore.FindAsync(sourceId))!.Status == SagaStatus.Completed);
            var completedStep = (await sourceStore.FindAsync(sourceId))!.Steps[0];
            Assert.Null(completedStep.Result);
            Assert.Equal(expected, completedStep.InvocationProgress!.Status);
            Assert.NotNull(completedStep.CompletedAt);
        }
        finally { await sourceHost.StopAsync(); await targetHost.StopAsync(); }
    }

    private async Task<SagaCommand> ReadParticipant(string queue)
    {
        BasicGetResult? delivery = null;
        await Wait(async () =>
        {
            await using var channel = await broker.Connection.CreateChannelAsync();
            delivery = await channel.BasicGetAsync(queue, true);
            return delivery is not null;
        });
        return RabbitMqMessageSerializer.DeserializeCommand(delivery!);
    }
    private async Task SendParticipantOutcome(string domain, SagaCommand command, bool success)
    {
        await using var channel = await broker.Connection.CreateChannelAsync(new CreateChannelOptions(true, true));
        var outcome = new SagaOutcomeEvent { Headers = command.Headers, IsSuccess = success, Result = JsonSerializer.SerializeToElement(new { success }) };
        await channel.BasicPublishAsync("", domain + ".outcomes", true,
            RabbitMqMessageSerializer.OutcomeProperties(outcome), RabbitMqMessageSerializer.Serialize(outcome));
    }
    private async Task AssertQueueCount(string queue, uint expected)
    {
        await using var channel = await broker.Connection.CreateChannelAsync();
        Assert.Equal(expected, await channel.MessageCountAsync(queue));
    }

    [RabbitMqFact]
    public async Task DashboardApisProjectBothSidesOfActualCrossDomainExecution()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var target = Definition("target" + suffix, "remote", "targetStep" + suffix);
        var source = new SagaDefinition { Domain = "source" + suffix, Name = "local", Version = "1.0",
            BigPicture = [new() { Domain = target.Domain, Process = target.Name, Version = target.Version, Endpoint = "https://internal-definition-address", Relationship = "invokes" }],
            Steps = [new() { Name = "invoke" + suffix, Command = "InvokeRemote", External = true,
                Transitions = [new() { Event = "success", NextStep = "completed" }, new() { Event = "failure", NextStep = "compensation" }] }] };
        await using var targetHost = await StartDashboard(target);
        await using var sourceHost = await StartDashboard(source, target.Name);
        using var sourceClient = DashboardClient(sourceHost); using var targetClient = DashboardClient(targetHost);
        string sourceId;
        await using (var scope = sourceHost.Services.CreateAsyncScope())
            sourceId = (await scope.ServiceProvider.GetRequiredService<ISagaFlow>().StartAsync(new { allow = true, value = 42 })).ToString("N");
        var sourceStore = sourceHost.Services.GetRequiredService<ISagaStateStore>();
        await Wait(async () => (await sourceStore.FindAsync(sourceId))!.Steps[0].InvocationProgress!.Status == ExternalProcessStatus.Accepted);
        var accepted = await ApiDetail(sourceClient, sourceId);
        var relationship = accepted.GetProperty("steps")[0].GetProperty("invocation");
        Assert.Equal("Local", accepted.GetProperty("origin").GetString());
        Assert.Equal("Accepted", relationship.GetProperty("lastKnownStatus").GetString());
        Assert.Equal(target.Domain, relationship.GetProperty("target").GetProperty("domain").GetString());
        var targetId = relationship.GetProperty("targetSagaInstanceId").GetString()!;
        var targetView = await ApiDetail(targetClient, targetId);
        var incoming = targetView.GetProperty("incomingInvocation");
        Assert.Equal("External", targetView.GetProperty("origin").GetString());
        Assert.Equal(sourceId, incoming.GetProperty("sourceSagaInstanceId").GetString());
        Assert.Equal(accepted.GetProperty("correlationId").GetString(), incoming.GetProperty("sourceCorrelationId").GetString());
        Assert.NotEqual(incoming.GetProperty("sourceCorrelationId").GetString(), targetView.GetProperty("correlationId").GetString());
        Assert.Equal(relationship.GetProperty("invocationId").GetString(), incoming.GetProperty("invocationId").GetString());
        var command = await ReadParticipant(target.Steps[0].Name);
        Assert.Equal(targetId, command.Headers.SagaInstanceId);
        await SendParticipantOutcome(target.Domain, command, true);
        await Wait(async () => (await sourceStore.FindAsync(sourceId))!.Status == SagaStatus.Completed);
        var completed = await ApiDetail(sourceClient, sourceId);
        Assert.Equal("Completed", completed.GetProperty("steps")[0].GetProperty("invocation").GetProperty("lastKnownStatus").GetString());
        Assert.Equal("Completed", (await ApiDetail(targetClient, targetId)).GetProperty("status").GetString());
        using var sourceList = JsonDocument.Parse(await sourceClient.GetStringAsync("/sagaflow/api/instances"));
        using var targetList = JsonDocument.Parse(await targetClient.GetStringAsync("/sagaflow/api/instances"));
        Assert.Equal(sourceId, Assert.Single(sourceList.RootElement.EnumerateArray()).GetProperty("id").GetString());
        Assert.Equal(targetId, Assert.Single(targetList.RootElement.EnumerateArray()).GetProperty("id").GetString());
        Assert.DoesNotContain("internal-definition-address", completed.GetRawText() + targetView.GetRawText());
        Assert.DoesNotContain("publication", completed.GetRawText());
        await sourceHost.StopAsync(); await targetHost.StopAsync();
    }
    private async Task<WebApplication> StartDashboard(SagaDefinition definition, string? target = null, Action<IServiceCollection>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSagaFlowInMemoryStorage();
        builder.Services.AddSagaFlow(options => options.UseDashboard()); builder.Services.AddScoped<IProcessInvocationPolicy, Policy>();
        if (target is not null) { builder.Services.AddSingleton(new Target(target, false)); builder.Services.AddScoped<ILocalSagaStepHandler, Handler>(); }
        builder.Services.AddSagaFlowRabbitMq(options =>
        {
            options.HostName = broker.HostName; options.Port = broker.Port; options.UserName = broker.UserName;
            options.Password = broker.Password; options.VirtualHost = broker.VirtualHost; options.EnableProcessInvocations = true;
        });
        configure?.Invoke(builder.Services);
        var app = builder.Build(); app.Services.GetRequiredService<ProcessDefinitionRegistry>().RegisterLocalBatch([definition]);
        await app.StartAsync(); return app;
    }
    private static HttpClient DashboardClient(WebApplication app) => new()
    { BaseAddress = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
    private static async Task<JsonElement> ApiDetail(HttpClient client, string id)
    {
        using var json = JsonDocument.Parse(await client.GetStringAsync("/sagaflow/api/instances/" + id));
        using var relationships = JsonDocument.Parse(await client.GetStringAsync($"/sagaflow/api/instances/{id}/relationships"));
        var items = relationships.RootElement.GetProperty("items").EnumerateArray().ToArray();
        var detail = json.RootElement;
        var incoming = detail.GetProperty("incomingInvocation");
        if (incoming.ValueKind != JsonValueKind.Null)
        {
            var target = Assert.Single(items, i => i.GetProperty("perspective").GetString() == "Target");
            Assert.Equal(incoming.GetProperty("invocationId").GetString(), target.GetProperty("invocationId").GetString());
            Assert.Equal(detail.GetProperty("correlationId").GetString(), target.GetProperty("targetCorrelationId").GetString());
            Assert.Equal(JsonValueKind.Null, target.GetProperty("lastKnownStatus").ValueKind);
        }
        foreach (var step in detail.GetProperty("steps").EnumerateArray())
        {
            var invocation = step.GetProperty("invocation");
            if (invocation.ValueKind == JsonValueKind.Null) continue;
            var row = Assert.Single(items, i => i.GetProperty("perspective").GetString() == "Source"
                && i.GetProperty("invocationId").GetString() == invocation.GetProperty("invocationId").GetString());
            // Two HTTP calls can observe different revisions while broker outcomes are being processed.
            // Compare immutable linkage here; stable status/record isolation is checked at checkpoints.
            Assert.Equal(detail.GetProperty("correlationId").GetString(), row.GetProperty("sourceCorrelationId").GetString());
        }
        return json.RootElement.Clone();
    }

    private static SagaDefinition SourceDefinition(SagaDefinition target, string suffix) => new()
    {
        Domain = "source" + suffix, Name = "local", Version = "1.0",
        Steps = Definition("source", "local", "sourceStep" + suffix).Steps,
        BigPicture = [new() { Domain = target.Domain, Process = target.Name, Version = target.Version, Endpoint = "https://discovery", Relationship = "invokes" }]
    };

    private static SagaDefinition Definition(string domain, string process, string step) => new()
    {
        Domain = domain, Name = process, Version = "1.0",
        Steps = [new() { Name = step, Command = "Work", Transitions = [new() { Event = "success", NextStep = "completed" }] }]
    };
    private async Task<IHost> Start(SagaDefinition definition, string? target = null, bool failHandler = false, string? postgreSql = null)
    {
        var host = BuildHost(definition, target, failHandler, postgreSql);
        await host.StartAsync(); return host;
    }

    private IHost BuildHost(SagaDefinition definition, string? target, bool failHandler, string? postgreSql)
    {
        var builder = Host.CreateApplicationBuilder(); builder.Logging.ClearProviders();
        if (postgreSql is null) builder.Services.AddSagaFlowInMemoryStorage();
        builder.Services.AddSagaFlow(options =>
        {
            if (postgreSql is not null) options.Storage.UsePostgreSql(storage => storage.ConnectionString = postgreSql);
        });
        builder.Services.AddScoped<IProcessInvocationPolicy, Policy>();
        if (target is not null)
        {
            builder.Services.AddSingleton(new Target(target, failHandler));
            builder.Services.AddScoped<ILocalSagaStepHandler, Handler>();
        }
        builder.Services.AddSagaFlowRabbitMq(options =>
        {
            options.HostName = broker.HostName; options.Port = broker.Port; options.UserName = broker.UserName;
            options.Password = broker.Password; options.VirtualHost = broker.VirtualHost; options.EnableProcessInvocations = true;
        });
        var host = builder.Build();
        host.Services.GetRequiredService<ProcessDefinitionRegistry>().RegisterLocalBatch([definition]);
        return host;
    }
    private sealed record Target(string Process, bool Fail);
    private static async Task<SagaInstance> StartWithHeldRequestAsync(IHost host, int value = 0)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var id = await scope.ServiceProvider.GetRequiredService<ISagaFlow>().StartAsync(new { allow = true, value });
        var store = host.Services.GetRequiredService<ISagaStateStore>();
        var state = (await store.FindAsync(id))!;
        Assert.Equal(ExternalProcessStatus.Requested, state.Steps[0].InvocationProgress!.Status);
        await HoldRequestAsync(store, state);
        return state;
    }

    private static async Task<OutboxClaim> HoldRequestAsync(ISagaStateStore store, SagaInstance state)
    {
        var claim = Assert.Single(await store.ClaimOutboxAsync("parking-fixture", DateTimeOffset.UtcNow.AddMinutes(1),
            TimeSpan.FromMinutes(5), 1));
        Assert.Equal(state.Steps[0].Invocation!.Message.MessageId, claim.Message.MessageId);
        return claim;
    }

    private sealed class Handler(ISagaFlow flow, Target target) : ILocalSagaStepHandler
    {
        public async Task ExecuteAsync(SagaCommand command, CancellationToken ct)
        {
            if (command.Name is not ("Work" or "InvokeRemote")) return;
            await flow.InvokeAsync(target.Process, command.Payload, ct);
            if (target.Fail) throw new InvalidOperationException("Local handler failed after preparing invocation.");
        }
    }
    private sealed class Policy : IProcessInvocationPolicy
    {
        public ValueTask<string?> ValidateAsync(ProcessInvocationRequest request, CancellationToken ct) =>
            ValueTask.FromResult(request.Payload.GetProperty("allow").GetBoolean() ? null : "Domain admission rejected payload.");
    }
    private static async Task Wait(Func<Task<bool>> predicate)
    {
        var limit = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < limit) { if (await predicate()) return; await Task.Delay(30); }
        Assert.Fail("Cross-domain RabbitMQ condition timed out.");
    }
}
