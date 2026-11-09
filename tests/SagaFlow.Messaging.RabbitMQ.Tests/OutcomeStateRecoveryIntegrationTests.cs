using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using RabbitMQ.Client;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Messages;
using SagaFlow.Core.State;
using SagaFlow.Engine.Coordination;
using SagaFlow.Engine.Messaging;
using SagaFlow.Engine.ProcessDefinition;
using SagaFlow.Hosting.DependencyInjection;
using SagaFlow.Messaging.RabbitMQ.DependencyInjection;
using SagaFlow.Participants.Messaging;
using SagaFlow.Storage.PostgreSQL;
using SagaFlow.Storage.PostgreSQL.DependencyInjection;
using SagaFlow.Storage.PostgreSQL.Tests;

namespace SagaFlow.Messaging.RabbitMQ.Tests;

public sealed class OutcomeRecoveryTheoryAttribute : TheoryAttribute
{
    public OutcomeRecoveryTheoryAttribute() => Skip = OutcomeRecoveryFactAttribute.SkipReason;
}

public sealed class OutcomeRecoveryFactAttribute : FactAttribute
{
    internal static string? SkipReason => new RabbitMqFactAttribute().Skip ?? new PostgreSqlFactAttribute().Skip;
    public OutcomeRecoveryFactAttribute() => Skip = SkipReason;
}

[Collection(RabbitMqIntegrationCollection.Name)]
public sealed class OutcomeStateRecoveryIntegrationTests(RabbitMqBrokerFixture broker)
{
    [OutcomeRecoveryTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostStateAndUnackedPriorOutcomeNeverPublishASecondCommand(bool newerFirst)
    {
        await using var database = new PostgreSqlTestDatabase();
        await database.CreateAsync();
        using var definition = new RecoveryDefinition(threeSteps: true);
        var store = new PostgreSqlSagaStateStore(database.ConnectionString);
        await using var publisher = await broker.Connection.CreateChannelAsync(new CreateChannelOptions(true, true));
        await using var source = await broker.Connection.CreateChannelAsync();
        string id;
        ParticipantOutcome prior;
        ParticipantOutcome newer;
        BasicProperties priorProperties;
        BasicProperties newerProperties;

        await using (var runtime = await StartAsync(definition, database.ConnectionString))
        {
            id = await StartSagaAsync(runtime.Host);
            var first = await ReadCommandAsync(publisher, definition.Work);
            var transport = runtime.Host.Services.GetRequiredService<RabbitMqTransport>();
            // Take over source consumption while retaining the real command publisher.
            await transport.ConsumerChannel!.CloseAsync();
            prior = Outcome(first);
            priorProperties = ParticipantProtocol.OutcomeProperties(prior);
            await SendAsync(publisher, definition, prior, priorProperties, includeSnapshot: true);
            var delivery = Assert.IsType<BasicGetResult>(await source.BasicGetAsync(definition.ReplyQueue, autoAck: false));
            Assert.False(delivery.Redelivered);
            await using var scope = runtime.Host.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<OutcomeEventReceiver>().ReceiveAsync(
                RabbitMqMessageSerializer.DeserializeOutcome(delivery.Body.Span, delivery.BasicProperties));

            var second = await GetAsync(publisher, definition.Next);
            var committed = Assert.IsType<SagaInstance>(await store.FindAsync(id));
            Assert.Equal(SagaStepStatus.Completed, committed.Steps[0].Status);
            Assert.Equal(Header(second, "sf-ex-execution-id"), committed.Steps[1].Execution!.ExecutionId);
            Assert.Equal(second.BasicProperties.MessageId, committed.Steps[1].Execution!.CommandId);
            newer = Outcome(ParticipantProtocol.ReadCommand(second.Body.Span, second.BasicProperties));
            newerProperties = ParticipantProtocol.OutcomeProperties(newer);
            await PostgreSqlTestDatabase.ExecuteAsync(database.ConnectionString,
                "TRUNCATE sagaflow.instances, sagaflow.incoming_invocations");
        }

        await using var restarted = await StartAsync(definition, database.ConnectionString);
        if (newerFirst)
        {
            await SendAsync(publisher, definition, newer, newerProperties, includeSnapshot: true);
            await AssertSnapshotQuarantinedAsync(publisher, definition, newer, newerProperties);
        }
        // The first outcome was processed and the next command confirmed, but the original delivery was never ACKed.
        await source.CloseAsync();
        await AssertSnapshotQuarantinedAsync(publisher, definition, prior, priorProperties);
        if (!newerFirst)
        {
            await SendAsync(publisher, definition, newer, newerProperties, includeSnapshot: true);
            await AssertSnapshotQuarantinedAsync(publisher, definition, newer, newerProperties);
        }

        var restartedTransport = restarted.Host.Services.GetRequiredService<RabbitMqTransport>();
        await WaitForAsync(() => restartedTransport.QuarantineAcknowledgementCount == 2);
        Assert.Null(await store.FindAsync(id));
        foreach (var queue in new[] { definition.Work, definition.Next, definition.Third, definition.ReplyQueue })
            Assert.Equal(0u, await publisher.MessageCountAsync(queue));
    }

    [OutcomeRecoveryTheory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task CurrentOutcomeContinuesOnlyAfterAuthoritativePostgreSqlStateIsRestored(int lostAtStep, bool completeLoss)
    {
        await using var database = new PostgreSqlTestDatabase();
        await database.CreateAsync();
        using var definition = new RecoveryDefinition(threeSteps: true);
        var store = new PostgreSqlSagaStateStore(database.ConnectionString);
        await using var channel = await broker.Connection.CreateChannelAsync(new CreateChannelOptions(true, true));
        var queues = new[] { definition.Work, definition.Next, definition.Third };
        var dispatched = new List<ParticipantCommand>();
        string id;
        await using (var runtime = await StartAsync(definition, database.ConnectionString))
        {
            id = await StartSagaAsync(runtime.Host);
            for (var index = 0; index <= lostAtStep; index++)
            {
                var command = await ReadCommandAsync(channel, queues[index]);
                dispatched.Add(command);
                var outcome = Outcome(command);
                var properties = ParticipantProtocol.OutcomeProperties(outcome);
                Assert.True(properties.Headers!.ContainsKey("sf-ex-state-snapshot"));
                if (index == lostAtStep)
                {
                    var checkpoint = Assert.IsType<SagaInstance>(await store.FindAsync(id));
                    if (completeLoss) await PostgreSqlTestDatabase.ExecuteAsync(database.ConnectionString,
                        "TRUNCATE sagaflow.instances, sagaflow.incoming_invocations");
                    else await DeleteInstanceAsync(database.ConnectionString, id);
                    await SendAsync(channel, definition, outcome, properties, includeSnapshot: true);
                    await AssertSnapshotQuarantinedAsync(channel, definition, outcome, properties);
                    var transport = runtime.Host.Services.GetRequiredService<RabbitMqTransport>();
                    await WaitForAsync(() => transport.QuarantineAcknowledgementCount == 1);
                    Assert.Null(await store.FindAsync(id));
                    foreach (var queue in queues) Assert.Equal(0u, await channel.MessageCountAsync(queue));

                    // This checkpoint was read from the last authoritative database state, not from the outcome.
                    checkpoint.Revision = 0;
                    await store.SaveAsync(checkpoint);
                }
                await SendAsync(channel, definition, outcome, properties, includeSnapshot: index == lostAtStep);
            }

            SagaInstance? expected;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            {
                do
                {
                    expected = await store.FindAsync(id);
                    if (expected?.Steps[lostAtStep].Status == SagaStepStatus.Completed) break;
                    await Task.Delay(30, timeout.Token);
                } while (true);
            }
            Assert.Equal("original", expected.Context.GetProperty("secretContext").GetString());
            Assert.Equal(lostAtStep + 1, expected.Steps.Count(step => step.Status == SagaStepStatus.Completed));
            for (var index = 0; index <= lostAtStep; index++)
            {
                Assert.Equal(dispatched[index].Headers.StepId, expected.Steps[index].StepId);
                Assert.NotNull(expected.Steps[index].Execution);
                Assert.True(expected.Steps[index].Result!.Value.GetProperty("accepted").GetBoolean());
            }
            if (lostAtStep == 2)
                Assert.Equal(SagaStatus.Completed, expected.Status);
            else
            {
                var next = await ReadCommandAsync(channel, queues[lostAtStep + 1]);
                Assert.Equal(id, next.Headers.SagaInstanceId);
                Assert.Equal(SagaStatus.Running, expected.Status);
            }
            Assert.Null(await channel.BasicGetAsync(definition.Quarantine, autoAck: true));
            Assert.Null(await channel.BasicGetAsync(queues[lostAtStep], autoAck: true));
            if (lostAtStep == 2)
                foreach (var queue in queues) Assert.Null(await channel.BasicGetAsync(queue, autoAck: true));
        }

        await using var restarted = await StartAsync(definition, database.ConnectionString);
        Assert.Equal(lostAtStep == 2 ? SagaStatus.Completed : SagaStatus.Running,
            (await store.FindAsync(id))!.Status);
    }

    [OutcomeRecoveryFact]
    public async Task IncomingTargetContinuesAfterAuthoritativePostgreSqlClaimIsRestored()
    {
        await using var database = new PostgreSqlTestDatabase();
        await database.CreateAsync();
        using var definition = new RecoveryDefinition();
        var store = new PostgreSqlSagaStateStore(database.ConnectionString);
        await using var channel = await broker.Connection.CreateChannelAsync(new CreateChannelOptions(true, true));
        await using var runtime = await StartAsync(definition, database.ConnectionString);
        var identity = new ProcessInvocationIdentity(Guid.NewGuid().ToString("N"), "parent-correlation",
            new("parent", "source", "1.0"), Guid.NewGuid().ToString("N"), "invoke",
            new(definition.Domain, "recovery-process", "1.0"));
        var message = new InvocationMessage(identity, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        var request = new ProcessInvocationRequest(identity,
            JsonSerializer.SerializeToElement(new { secretContext = "incoming" }), message);
        await using var scope = runtime.Host.Services.CreateAsyncScope();
        var local = scope.ServiceProvider.GetRequiredService<ProcessDefinitionRegistry>().Snapshot()
            .Single(item => item.Origin == DefinitionOrigin.Local).Definition;
        var manager = scope.ServiceProvider.GetRequiredService<SagaInstanceManager>();
        var target = await store.GetOrCreateIncomingAsync(request, () => manager.Create(local, request.Payload), DateTimeOffset.UtcNow);
        await scope.ServiceProvider.GetRequiredService<SagaCoordinator>().StartIncomingAsync(target.Id);
        var command = await ReadCommandAsync(channel, definition.Work);
        var checkpoint = Assert.IsType<SagaInstance>(await store.FindAsync(target.Id));
        await PostgreSqlTestDatabase.ExecuteAsync(database.ConnectionString,
            "TRUNCATE sagaflow.instances, sagaflow.incoming_invocations");
        var outcome = Outcome(command);
        var properties = ParticipantProtocol.OutcomeProperties(outcome);
        await SendAsync(channel, definition, outcome, properties, includeSnapshot: true);
        await AssertSnapshotQuarantinedAsync(channel, definition, outcome, properties);
        var transport = runtime.Host.Services.GetRequiredService<RabbitMqTransport>();
        await WaitForAsync(() => transport.QuarantineAcknowledgementCount == 1);
        Assert.Empty(await store.ListAsync());
        Assert.Equal(0u, await channel.MessageCountAsync(definition.Next));

        checkpoint.Revision = 0;
        Assert.True(await store.TryRecoverIncomingAsync(checkpoint));
        await SendAsync(channel, definition, outcome, properties, includeSnapshot: true);
        var next = await ReadCommandAsync(channel, definition.Next);
        Assert.Equal(target.Id, next.Headers.SagaInstanceId);
        var recovered = (await store.FindAsync(target.Id))!;
        Assert.Equal(SagaStepStatus.Completed, recovered.Steps[0].Status);
        Assert.Equal(identity, recovered.IncomingInvocation!.Identity);
        Assert.Equal(message, recovered.IncomingInvocation.RequestMessage);
        var duplicate = await store.GetOrCreateIncomingAsync(request,
            () => throw new Exception("Recovered invocation must retain its claim."), DateTimeOffset.UtcNow);
        Assert.Equal(target.Id, duplicate.Id);
        Assert.Single(await store.ListAsync());
        Assert.Null(await channel.BasicGetAsync(definition.Quarantine, autoAck: true));
    }

    [OutcomeRecoveryTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentConnectionsApplyOutcomeAtMostOnceAndOnlyWithAuthoritativeState(bool missing)
    {
        await using var database = new PostgreSqlTestDatabase();
        await database.CreateAsync();
        using var definition = new RecoveryDefinition();
        await using var channel = await broker.Connection.CreateChannelAsync(new CreateChannelOptions(true, true));
        await using var runtime = await StartAsync(definition, database.ConnectionString);
        var id = await StartSagaAsync(runtime.Host);
        var first = await ReadCommandAsync(channel, definition.Work);
        await SendAsync(channel, definition, Outcome(first));
        var final = await ReadCommandAsync(channel, definition.Next);
        var participantOutcome = Outcome(final);
        var outcome = RabbitMqMessageSerializer.DeserializeOutcome(JsonSerializer.SerializeToUtf8Bytes(participantOutcome.Result),
            ParticipantProtocol.OutcomeProperties(participantOutcome));
        var store = new PostgreSqlSagaStateStore(database.ConnectionString);
        var revision = Assert.IsType<SagaInstance>(await store.FindAsync(id)).Revision;
        var gate = missing ? new ConcurrentMissingStore(database.ConnectionString) : null;
        if (missing) await DeleteInstanceAsync(database.ConnectionString, id);
        using var left = BuildReceiver(definition, (ISagaStateStore?)gate ?? store);
        using var right = BuildReceiver(definition, (ISagaStateStore?)gate ?? new PostgreSqlSagaStateStore(database.ConnectionString));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var deliveries = new[] { left.Receiver.ReceiveAsync(outcome, deadline.Token),
            right.Receiver.ReceiveAsync(outcome, deadline.Token) };
        var results = await Task.WhenAll(deliveries.Select(async delivery =>
        {
            try { await delivery; return (Exception?)null; }
            catch (Exception error) { return error; }
        }));

        if (gate is not null)
        {
            Assert.All(results, error => Assert.Contains("cannot prove", Assert.IsType<SagaOutcomeRecoveryRequiredException>(error).Message));
            Assert.Equal(2, gate.MissingReads);
            Assert.Empty(await store.ListAsync());
        }
        else
        {
            Assert.Single(results, error => error is null);
            Assert.Single(results, error => error is DuplicateSagaOutcomeException);
            var state = Assert.Single(await store.ListAsync());
            Assert.Equal(revision + 1, state.Revision);
            Assert.Equal(SagaStatus.Completed, state.Status);
            Assert.Equal(SagaStepStatus.Completed, state.Steps[0].Status);
            Assert.Equal(SagaStepStatus.Completed, state.Steps[1].Status);
        }
        Assert.Null(await channel.BasicGetAsync(definition.Work, autoAck: true));
        Assert.Null(await channel.BasicGetAsync(definition.Next, autoAck: true));
    }

    [OutcomeRecoveryTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedParticipantOutcomesRecoverEvidenceAfterSingleOrCompletePostgreSqlLoss(bool completeLoss)
    {
        await using var database = new PostgreSqlTestDatabase();
        await database.CreateAsync();
        using var definition = new RecoveryDefinition();
        var store = new PostgreSqlSagaStateStore(database.ConnectionString);
        await using var channel = await broker.Connection.CreateChannelAsync(new CreateChannelOptions(true, true));
        string id;
        ParticipantCommand retained;
        ParticipantOutcome outcome;
        await using (var runtime = await StartAsync(definition, database.ConnectionString))
        {
            id = await StartSagaAsync(runtime.Host);
            retained = await ReadCommandAsync(channel, definition.Work);
            Assert.Equal(id, retained.Headers.SagaInstanceId);
            Assert.Equal("original", retained.Payload.GetProperty("secretContext").GetString());
            var another = await StartSagaAsync(runtime.Host);
            var anotherCommand = await ReadCommandAsync(channel, definition.Work);
            Assert.Equal(another, anotherCommand.Headers.SagaInstanceId);

            if (completeLoss)
            {
                await PostgreSqlTestDatabase.ExecuteAsync(database.ConnectionString,
                    "TRUNCATE sagaflow.instances, sagaflow.incoming_invocations");
                Assert.Empty(await store.ListAsync());
            }
            else await DeleteInstanceAsync(database.ConnectionString, id);
            Assert.Null(await store.FindAsync(id));

            outcome = Outcome(retained);
            await SendAsync(channel, definition, outcome);
            await AssertQuarantinedAsync(channel, definition, outcome);
            var recovered = Assert.IsType<SagaInstance>(await store.FindAsync(id));
            AssertRecovered(recovered, retained, outcome);
            Assert.Null(await channel.BasicGetAsync(definition.Next, autoAck: true));

            await SendAsync(channel, definition, Outcome(anotherCommand));
            if (completeLoss)
            {
                _ = await GetAsync(channel, definition.Quarantine);
                Assert.Equal(2, (await store.ListAsync()).Count);
                Assert.All(await store.ListAsync(), item => Assert.Equal(SagaStatus.RecoveryRequired, item.Status));
            }
            else
            {
                var next = await ReadCommandAsync(channel, definition.Next);
                Assert.Equal(another, next.Headers.SagaInstanceId);
                var normal = (await store.FindAsync(another))!;
                Assert.Equal(SagaStepStatus.Completed, normal.Steps[0].Status);
                Assert.Equal("original", normal.Context.GetProperty("secretContext").GetString());
                Assert.Null(normal.RecoveryOutcome);
            }
            Assert.Null(await channel.BasicGetAsync(definition.Work, autoAck: true));
        }

        await using var restarted = await StartAsync(definition, database.ConnectionString);
        AssertRecovered((await store.FindAsync(id))!, retained, outcome);
        await SendAsync(channel, definition, outcome);
        await AssertQuarantinedAsync(channel, definition, outcome);
        AssertRecovered((await store.FindAsync(id))!, retained, outcome);
        Assert.Null(await channel.BasicGetAsync(definition.Work, autoAck: true));
        Assert.Null(await channel.BasicGetAsync(definition.Next, autoAck: true));
    }

    [OutcomeRecoveryFact]
    public async Task CompensationLossPreservesTheReportedExecutionWithoutRestoringAPlan()
    {
        await using var database = new PostgreSqlTestDatabase();
        await database.CreateAsync();
        using var definition = new RecoveryDefinition();
        var store = new PostgreSqlSagaStateStore(database.ConnectionString);
        await using var runtime = await StartAsync(definition, database.ConnectionString);
        await using var channel = await broker.Connection.CreateChannelAsync(new CreateChannelOptions(true, true));
        var id = await StartSagaAsync(runtime.Host);
        var work = await ReadCommandAsync(channel, definition.Work);
        await SendAsync(channel, definition, Outcome(work));
        var next = await ReadCommandAsync(channel, definition.Next);
        await SendAsync(channel, definition, Outcome(next, success: false));
        var compensation = await ReadCommandAsync(channel, definition.Work);
        Assert.True(compensation.Headers.IsCompensation);
        Assert.Equal("UndoWork", compensation.Name);
        var dispatched = (await store.FindAsync(id))!;
        var forwardExecutionId = dispatched.Steps[0].Execution!.ExecutionId;
        Assert.Equal(forwardExecutionId, dispatched.Compensations.Single().Execution!.ForwardExecutionId);

        await DeleteInstanceAsync(database.ConnectionString, id);
        var outcome = Outcome(compensation);
        await SendAsync(channel, definition, outcome);
        await AssertQuarantinedAsync(channel, definition, outcome);
        var recovered = (await store.FindAsync(id))!;
        AssertRecovered(recovered, compensation, outcome);
        Assert.Equal(forwardExecutionId, recovered.RecoveryOutcome!.Headers.ForwardExecutionId);
        Assert.All(recovered.Steps, step => Assert.Null(step.Execution));
        Assert.Null(await channel.BasicGetAsync(definition.Work, autoAck: true));
        Assert.Null(await channel.BasicGetAsync(definition.Next, autoAck: true));
    }

    [OutcomeRecoveryFact]
    public async Task MismatchedVersionAndMissingWireMetadataArePreservedWithoutCreatingState()
    {
        await using var database = new PostgreSqlTestDatabase();
        await database.CreateAsync();
        using var definition = new RecoveryDefinition();
        var store = new PostgreSqlSagaStateStore(database.ConnectionString);
        await using var runtime = await StartAsync(definition, database.ConnectionString);
        await using var channel = await broker.Connection.CreateChannelAsync(new CreateChannelOptions(true, true));
        var id = await StartSagaAsync(runtime.Host);
        var command = await ReadCommandAsync(channel, definition.Work);
        var outcome = Outcome(command);
        await DeleteInstanceAsync(database.ConnectionString, id);

        var properties = ParticipantProtocol.OutcomeProperties(outcome);
        properties.Headers!["sf-ex-process-version"] = "2.0";
        await SendAsync(channel, definition, outcome, properties);
        var mismatched = await AssertQuarantinedAsync(channel, definition, outcome);
        Assert.Equal("2.0", Header(mismatched, "sf-ex-process-version"));
        Assert.Null(await store.FindAsync(id));

        properties = ParticipantProtocol.OutcomeProperties(outcome);
        properties.Headers!.Remove("sf-version");
        await SendAsync(channel, definition, outcome, properties);
        var unversioned = await GetAsync(channel, definition.Quarantine);
        Assert.Equal("missing-or-unsupported-wire-version", Header(unversioned, "sf-quarantine-reason"));
        Assert.Equal(outcome.MessageId, unversioned.BasicProperties.MessageId);
        Assert.Null(await store.FindAsync(id));
        Assert.Null(await channel.BasicGetAsync(definition.Next, autoAck: true));
    }

    [OutcomeRecoveryFact]
    public async Task ConcurrentPostgreSqlConnectionsCreateOneSnapshotAndClassifyConflictingEvidence()
    {
        await using var database = new PostgreSqlTestDatabase();
        await database.CreateAsync();
        using var definition = new RecoveryDefinition();
        await using var runtime = await StartAsync(definition, database.ConnectionString);
        await using var channel = await broker.Connection.CreateChannelAsync(new CreateChannelOptions(true, true));
        var id = await StartSagaAsync(runtime.Host);
        var retained = await ReadCommandAsync(channel, definition.Work);
        var participantOutcome = Outcome(retained);
        var legacyProperties = ParticipantProtocol.OutcomeProperties(participantOutcome);
        legacyProperties.Headers!.Remove("sf-ex-state-snapshot");
        var outcome = RabbitMqMessageSerializer.DeserializeOutcome(JsonSerializer.SerializeToUtf8Bytes(participantOutcome.Result),
            legacyProperties);
        await DeleteInstanceAsync(database.ConnectionString, id);

        var gate = new ConcurrentMissingStore(database.ConnectionString);
        using var first = BuildReceiver(definition, gate);
        using var second = BuildReceiver(definition, gate);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await Task.WhenAll(
            Assert.ThrowsAsync<SagaOutcomeRecoveryRequiredException>(() => first.Receiver.ReceiveAsync(outcome, deadline.Token)),
            Assert.ThrowsAsync<SagaOutcomeRecoveryRequiredException>(() => second.Receiver.ReceiveAsync(outcome, deadline.Token)));
        Assert.Equal(2, gate.MissingReads);
        var store = new PostgreSqlSagaStateStore(database.ConnectionString);
        AssertRecovered(Assert.Single(await store.ListAsync()), retained, participantOutcome);

        var conflicting = new SagaOutcomeEvent
        {
            MessageId = outcome.MessageId, CausationId = outcome.CausationId, Name = outcome.Name,
            Headers = outcome.Headers, IsSuccess = false, Result = outcome.Result
        };
        var conflict = await Assert.ThrowsAsync<SagaOutcomeRecoveryRequiredException>(() => first.Receiver.ReceiveAsync(conflicting));
        Assert.True(conflict.IsConflictingEvidence);
        AssertRecovered(Assert.Single(await store.ListAsync()), retained, participantOutcome);
        Assert.Null(await channel.BasicGetAsync(definition.Next, autoAck: true));
    }

    [OutcomeRecoveryTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ConflictingWireOutcomeIsQuarantinedWithItsOriginalEvidence(bool failFirstTransfer, bool incompatibleVersion)
    {
        await using var database = new PostgreSqlTestDatabase();
        await database.CreateAsync();
        using var definition = new RecoveryDefinition();
        var store = new PostgreSqlSagaStateStore(database.ConnectionString);
        await using var channel = await broker.Connection.CreateChannelAsync(new CreateChannelOptions(true, true));
        ParticipantCommand command;
        ParticipantOutcome first;
        ParticipantOutcome conflicting;
        BasicProperties conflictProperties;
        string id;
        string snapshot;
        await using (var runtime = await StartAsync(definition, database.ConnectionString))
        {
            id = await StartSagaAsync(runtime.Host);
            command = await ReadCommandAsync(channel, definition.Work);
            await DeleteInstanceAsync(database.ConnectionString, id);

            first = Outcome(command);
            await SendAsync(channel, definition, first);
            await AssertQuarantinedAsync(channel, definition, first);
            var transport = runtime.Host.Services.GetRequiredService<RabbitMqTransport>();
            await WaitForAsync(() => transport.QuarantineAcknowledgementCount == 1);
            var recovered = Assert.IsType<SagaInstance>(await store.FindAsync(id));
            AssertRecovered(recovered, command, first);
            snapshot = JsonSerializer.Serialize(recovered);

            conflicting = Outcome(command, success: false);
            conflictProperties = ParticipantProtocol.OutcomeProperties(conflicting);
            if (incompatibleVersion) conflictProperties.Headers!["sf-ex-process-version"] = "opaque-version";
            if (failFirstTransfer) await channel.QueueDeleteAsync(definition.Quarantine);
            await SendAsync(channel, definition, conflicting, conflictProperties);

            if (failFirstTransfer)
            {
                await WaitForAsync(() => transport.QuarantineFailureCount == 1);
                Assert.Equal(1, transport.QuarantineAcknowledgementCount);
            }
            else
            {
                await AssertConflictQuarantinedAsync(channel, definition, conflicting, conflictProperties);
                await WaitForAsync(() => transport.QuarantineAcknowledgementCount == 2);
                Assert.Null(await channel.BasicGetAsync(definition.ReplyQueue, autoAck: true));
            }
        }

        if (failFirstTransfer)
        {
            await using var restarted = await StartAsync(definition, database.ConnectionString);
            await AssertConflictQuarantinedAsync(channel, definition, conflicting, conflictProperties);
            var transport = restarted.Host.Services.GetRequiredService<RabbitMqTransport>();
            await WaitForAsync(() => transport.QuarantineAcknowledgementCount == 1);
            Assert.Null(await channel.BasicGetAsync(definition.ReplyQueue, autoAck: true));
        }

        var unchanged = Assert.Single(await store.ListAsync());
        Assert.Equal(snapshot, JsonSerializer.Serialize(unchanged));
        AssertRecovered(unchanged, command, first);
        Assert.Null(await channel.BasicGetAsync(definition.Work, autoAck: true));
        Assert.Null(await channel.BasicGetAsync(definition.Next, autoAck: true));
    }

    [OutcomeRecoveryFact]
    public async Task PostgreSqlConnectionFailureDoesNotTriggerReconstruction()
    {
        await using var database = new PostgreSqlTestDatabase();
        await database.CreateAsync();
        using var definition = new RecoveryDefinition();
        await using var runtime = await StartAsync(definition, database.ConnectionString);
        await using var channel = await broker.Connection.CreateChannelAsync(new CreateChannelOptions(true, true));
        var id = await StartSagaAsync(runtime.Host);
        var command = await ReadCommandAsync(channel, definition.Work);
        var participantOutcome = Outcome(command);
        var outcome = RabbitMqMessageSerializer.DeserializeOutcome(JsonSerializer.SerializeToUtf8Bytes(participantOutcome.Result),
            ParticipantProtocol.OutcomeProperties(participantOutcome));
        var unavailable = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Database = $"missing_{Guid.NewGuid():N}" };
        using var receiver = BuildReceiver(definition, new PostgreSqlSagaStateStore(unavailable.ConnectionString));
        var error = await Assert.ThrowsAsync<PostgresException>(() => receiver.Receiver.ReceiveAsync(outcome));
        Assert.Equal(PostgresErrorCodes.InvalidCatalogName, error.SqlState);

        var existing = (await new PostgreSqlSagaStateStore(database.ConnectionString).FindAsync(id))!;
        Assert.Equal(SagaStatus.Running, existing.Status);
        Assert.Null(existing.RecoveryOutcome);
        Assert.Null(await channel.BasicGetAsync(definition.Next, autoAck: true));
    }

    [OutcomeRecoveryFact]
    public async Task FailedQuarantineTransferLeavesTheRecoveryOutcomeAvailableForRedelivery()
    {
        await using var database = new PostgreSqlTestDatabase();
        await database.CreateAsync();
        using var definition = new RecoveryDefinition();
        var store = new PostgreSqlSagaStateStore(database.ConnectionString);
        await using var channel = await broker.Connection.CreateChannelAsync(new CreateChannelOptions(true, true));
        ParticipantCommand command;
        ParticipantOutcome outcome;
        string id;
        await using (var runtime = await StartAsync(definition, database.ConnectionString))
        {
            id = await StartSagaAsync(runtime.Host);
            command = await ReadCommandAsync(channel, definition.Work);
            outcome = Outcome(command);
            await DeleteInstanceAsync(database.ConnectionString, id);
            await channel.QueueDeleteAsync(definition.Quarantine);
            await SendAsync(channel, definition, outcome);

            var transport = runtime.Host.Services.GetRequiredService<RabbitMqTransport>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (transport.QuarantineFailureCount == 0)
                await Task.Delay(30, timeout.Token);
            Assert.Equal(0, transport.QuarantineAcknowledgementCount);
            AssertRecovered((await store.FindAsync(id))!, command, outcome);
        }

        await using var restarted = await StartAsync(definition, database.ConnectionString);
        await AssertQuarantinedAsync(channel, definition, outcome);
        AssertRecovered((await store.FindAsync(id))!, command, outcome);
        Assert.Null(await channel.BasicGetAsync(definition.Next, autoAck: true));
    }

    private async Task<RecoveryHost> StartAsync(RecoveryDefinition definition, string connectionString)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(broker.Connection);
        builder.Services.AddSagaFlow(options =>
        {
            options.LoadConfiguration(definition.Path);
            options.Storage.UsePostgreSql(storage => storage.ConnectionString = connectionString);
        });
        builder.Services.AddSagaFlowRabbitMqUsingHostConnection(options => options.CommandExchange = definition.Exchange);
        var runtime = new RecoveryHost(builder.Build());
        try { await runtime.Host.StartAsync(); return runtime; }
        catch { await runtime.DisposeAsync(); throw; }
    }

    private static ReceiverScope BuildReceiver(RecoveryDefinition definition, ISagaStateStore store)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSagaFlow();
        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ProcessDefinitionRegistry>().RegisterLocalBatch([
            new DefinitionYamlCodec().Parse(File.ReadAllText(definition.Path))]);
        return new(provider);
    }

    private static async Task<string> StartSagaAsync(IHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<ISagaFlow>().StartAsync(new { secretContext = "original" })).ToString("N");
    }

    private static async Task DeleteInstanceAsync(string connectionString, string id)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("DELETE FROM sagaflow.instances WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static ParticipantOutcome Outcome(ParticipantCommand command, bool success = true) => new()
    {
        Headers = command.Headers, Name = "WorkOutcome", IsSuccess = success,
        Result = JsonSerializer.SerializeToElement(new { accepted = success })
    };

    private static ValueTask SendAsync(IChannel channel, RecoveryDefinition definition, ParticipantOutcome outcome,
        BasicProperties? properties = null, bool includeSnapshot = false)
    {
        properties ??= ParticipantProtocol.OutcomeProperties(outcome);
        if (!includeSnapshot) properties.Headers?.Remove("sf-ex-state-snapshot");
        return channel.BasicPublishAsync("", definition.ReplyQueue, true, properties,
            JsonSerializer.SerializeToUtf8Bytes(outcome.Result));
    }

    private static async Task<ParticipantCommand> ReadCommandAsync(IChannel channel, string queue)
    {
        var delivery = await GetAsync(channel, queue);
        return ParticipantProtocol.ReadCommand(delivery.Body.Span, delivery.BasicProperties);
    }

    private static async Task<BasicGetResult> AssertQuarantinedAsync(IChannel channel, RecoveryDefinition definition,
        ParticipantOutcome outcome, string reason = "outcome-recovery-required")
    {
        var delivery = await GetAsync(channel, definition.Quarantine);
        Assert.Equal(reason, Header(delivery, "sf-quarantine-reason"));
        Assert.Equal(outcome.MessageId, delivery.BasicProperties.MessageId);
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(outcome.Result), delivery.Body.ToArray());
        return delivery;
    }

    private static Task AssertConflictQuarantinedAsync(IChannel channel, RecoveryDefinition definition,
        ParticipantOutcome outcome, BasicProperties original) =>
        AssertSnapshotQuarantinedAsync(channel, definition, outcome, original, "outcome-recovery-conflict");

    private static async Task AssertSnapshotQuarantinedAsync(IChannel channel, RecoveryDefinition definition,
        ParticipantOutcome outcome, BasicProperties original, string reason = "outcome-recovery-required")
    {
        var delivery = await AssertQuarantinedAsync(channel, definition, outcome, reason);
        Assert.Equal(original.CorrelationId, delivery.BasicProperties.CorrelationId);
        Assert.Equal(original.Type, delivery.BasicProperties.Type);
        Assert.Equal(original.Timestamp, delivery.BasicProperties.Timestamp);
        foreach (var (key, value) in original.Headers!.Where(header => header.Key.StartsWith("sf-", StringComparison.Ordinal)))
        {
            var expected = value as byte[] ?? System.Text.Encoding.UTF8.GetBytes(Assert.IsType<string>(value));
            Assert.Equal(expected, Assert.IsType<byte[]>(delivery.BasicProperties.Headers![key]));
        }
    }

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!predicate()) await Task.Delay(30, timeout.Token);
    }

    private static string Header(BasicGetResult delivery, string key) =>
        System.Text.Encoding.UTF8.GetString(Assert.IsType<byte[]>(delivery.BasicProperties.Headers![key]));

    private static async Task<BasicGetResult> GetAsync(IChannel channel, string queue)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            if (await channel.BasicGetAsync(queue, autoAck: true, timeout.Token) is { } delivery) return delivery;
            await Task.Delay(30, timeout.Token);
        }
    }

    private static void AssertRecovered(SagaInstance state, ParticipantCommand retained, ParticipantOutcome outcome)
    {
        Assert.Equal(retained.Headers.SagaInstanceId, state.Id);
        Assert.Equal(retained.Headers.CorrelationId, state.CorrelationId);
        Assert.Equal(SagaStatus.RecoveryRequired, state.Status);
        Assert.Equal(1, state.Revision);
        Assert.Null(state.CurrentStep);
        Assert.Null(state.IncomingInvocation);
        Assert.Null(state.DomainRollback);
        Assert.Null(state.PayloadType);
        Assert.Equal(default, state.CreatedAt);
        Assert.Equal(JsonValueKind.Null, state.Context.ValueKind);
        Assert.Empty(state.Compensations);
        Assert.All(state.Steps, step =>
        {
            Assert.Equal(SagaStepStatus.Unknown, step.Status);
            Assert.Null(step.ExecutionOrder);
            Assert.Null(step.Invocation);
            Assert.Null(step.Result);
            Assert.Null(step.Input);
        });
        var evidence = Assert.IsType<SagaOutcomeEvent>(state.RecoveryOutcome);
        var expected = RabbitMqMessageSerializer.DeserializeOutcome(JsonSerializer.SerializeToUtf8Bytes(outcome.Result),
            ParticipantProtocol.OutcomeProperties(outcome));
        Assert.Equal(expected.Headers, evidence.Headers);
        Assert.Equal(expected.MessageId, evidence.MessageId);
        Assert.Equal(expected.IsSuccess, evidence.IsSuccess);
        Assert.Equal(expected.Result.GetRawText(), evidence.Result.GetRawText());
        Assert.Equal(evidence.Headers.ProcessDomain, state.DefinitionDomain);
        Assert.Equal(evidence.Headers.ProcessName, state.DefinitionName);
        Assert.Equal(evidence.Headers.ProcessVersion, state.DefinitionVersion);
        if (!expected.Headers.IsCompensation)
            Assert.Equal(expected.Headers, state.Steps.Single(step => step.StepId == expected.Headers.StepId).Execution);
    }

    private sealed class RecoveryHost(IHost host) : IAsyncDisposable
    {
        internal IHost Host { get; } = host;
        public async ValueTask DisposeAsync()
        {
            try { await Host.StopAsync(); }
            finally { await ((IAsyncDisposable)Host).DisposeAsync(); }
        }
    }

    private sealed class ReceiverScope : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly IServiceScope _scope;
        internal ReceiverScope(ServiceProvider provider) { _provider = provider; _scope = provider.CreateScope(); }
        internal OutcomeEventReceiver Receiver => _scope.ServiceProvider.GetRequiredService<OutcomeEventReceiver>();
        public void Dispose() { _scope.Dispose(); _provider.Dispose(); }
    }

    private sealed class ConcurrentMissingStore(string connectionString) : ISagaStateStore
    {
        private readonly PostgreSqlSagaStateStore _store = new(connectionString);
        private readonly TaskCompletionSource _bothMissing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _missingReads;
        internal int MissingReads => _missingReads;

        public async ValueTask<SagaInstance?> FindAsync(string id, CancellationToken cancellationToken = default)
        {
            var state = await _store.FindAsync(id, cancellationToken);
            if (state is not null) return state;
            if (Interlocked.Increment(ref _missingReads) == 2) _bothMissing.SetResult();
            await _bothMissing.Task.WaitAsync(cancellationToken);
            return null;
        }

        public ValueTask SaveAsync(SagaInstance instance, CancellationToken cancellationToken = default) =>
            _store.SaveAsync(instance, cancellationToken);
        public ValueTask<IReadOnlyList<SagaInstance>> ListAsync(CancellationToken cancellationToken = default) =>
            _store.ListAsync(cancellationToken);
        public ValueTask<TResult> UpdateAsync<TResult>(string id, Func<SagaInstance, TResult> update, CancellationToken cancellationToken = default) =>
            _store.UpdateAsync(id, update, cancellationToken);
    }

    private sealed class RecoveryDefinition : IDisposable
    {
        internal string Domain { get; } = $"recovery-{Guid.NewGuid():N}";
        internal string Work => $"{Domain}-work";
        internal string Next => $"{Domain}-next";
        internal string Third => $"{Domain}-third";
        internal string Exchange => $"{Domain}.commands";
        internal string ReplyQueue => $"{Domain}.outcomes";
        internal string Quarantine => $"{ReplyQueue}.quarantine";
        internal string Path { get; } = System.IO.Path.GetTempFileName();

        internal RecoveryDefinition(bool threeSteps = false)
        {
            var yaml = $$"""
            name: recovery-process
            domain: {{Domain}}
            version: "1.0"
            steps:
              - name: {{Work}}
                command: DoWork
                compensation:
                  command: UndoWork
                transitions:
                  - event: success
                    nextStep: {{Next}}
                  - event: failure
                    nextStep: compensation
              - name: {{Next}}
                command: DoNext
                transitions:
                  - event: success
                    nextStep: {{(threeSteps ? Third : "completed")}}
                  - event: failure
                    nextStep: compensation
            """;
            if (threeSteps) yaml += $"\n  - name: {Third}\n    command: DoFinal\n    transitions:\n      - event: success\n        nextStep: completed\n      - event: failure\n        nextStep: compensation\n";
            File.WriteAllText(Path, yaml);
        }

        public void Dispose() => File.Delete(Path);
    }
}
