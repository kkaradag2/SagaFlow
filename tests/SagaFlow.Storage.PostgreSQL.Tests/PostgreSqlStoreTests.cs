using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Definitions;
using SagaFlow.Core.Messages;
using SagaFlow.Core.State;
using SagaFlow.Engine.ContextState;
using SagaFlow.Engine.Messaging;
using SagaFlow.Engine.ProcessDefinition;
using SagaFlow.Hosting.DependencyInjection;
using SagaFlow.Storage.PostgreSQL.DependencyInjection;
using SagaFlow.Storage.PostgreSQL.Schema;

namespace SagaFlow.Storage.PostgreSQL.Tests;

public sealed class PostgreSqlStoreTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.FromHours(3)).AddTicks(7);
    private readonly PostgreSqlTestDatabase _database = new();
    private ISagaStateStore OpenStore() => new PostgreSqlSagaStateStore(_database.ConnectionString);

    [PostgreSqlFact]
    public async Task OutboxCommitRollbackAndConcurrentClaimsUseOneTransaction()
    {
        var store = OpenStore();
        var state = Target();
        await store.SaveAsync(state);
        var created = DateTimeOffset.UtcNow.AddMinutes(-1);
        OutboxMessage Message(string id) => new()
        {
            MessageId = id, Name = "Work", Destination = "work", Body = [123, 125], RecoverySnapshot = [1],
            Headers = new() { ProtocolVersion = 1, SagaInstanceId = state.Id, StepId = "work",
                CommandId = id, CommandName = "Work", ExecutionId = "execution", CreatedAt = created }
        };

        await Assert.ThrowsAsync<IOException>(async () => await store.UpdateWithOutboxAsync<int>(state.Id, working =>
        {
            working.CurrentStep = "uncommitted";
            throw new IOException("before commit");
        }));
        Assert.Null((await store.FindAsync(state.Id))!.CurrentStep);

        await store.UpdateWithOutboxAsync(state.Id, working =>
        {
            working.CurrentStep = "work";
            return (true, Message("command"));
        });
        Assert.Equal("work", (await store.FindAsync(state.Id))!.CurrentStep);
        await Assert.ThrowsAnyAsync<Exception>(async () => await store.UpdateWithOutboxAsync(state.Id, working =>
        {
            working.CurrentStep = "wrong";
            return (true, Message("command"));
        }));
        Assert.Equal("work", (await store.FindAsync(state.Id))!.CurrentStep);

        var a = OpenStore();
        var b = OpenStore();
        var slowClock = DateTimeOffset.UtcNow.AddHours(-12);
        var fastClock = DateTimeOffset.UtcNow.AddHours(12);
        var lease = TimeSpan.FromMinutes(1);
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var timeQuery = new NpgsqlCommand("SELECT clock_timestamp()", connection);
        var before = new DateTimeOffset((DateTime)(await timeQuery.ExecuteScalarAsync())!);
        var claimed = await Task.WhenAll(a.ClaimOutboxAsync("a", slowClock, lease, 1).AsTask(),
            b.ClaimOutboxAsync("b", fastClock, lease, 1).AsTask());
        var first = Assert.Single(claimed.SelectMany(x => x));
        var after = new DateTimeOffset((DateTime)(await timeQuery.ExecuteScalarAsync())!);
        Assert.InRange(first.LeaseUntil, before + lease, after + lease);
        Assert.Empty(await b.ClaimOutboxAsync("c", fastClock.AddHours(1), lease, 1));

        // Move the stored lease past PostgreSQL's clock without advancing a dispatcher clock.
        await PostgreSqlTestDatabase.ExecuteAsync(_database.ConnectionString, """
            UPDATE sagaflow.outbox SET lease_until = clock_timestamp() - interval '1 second'
            WHERE message_id = 'command'
            """);
        var recovered = Assert.Single(await a.ClaimOutboxAsync("c", slowClock, lease, 1));
        Assert.Equal("command", recovered.Message.MessageId);
        Assert.Equal(2, recovered.Attempts);
        await Assert.ThrowsAsync<OutboxClaimLostException>(async () =>
            await a.MarkOutboxPublishedAsync("command", first.Owner, DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<OutboxClaimLostException>(async () =>
            await a.ReleaseOutboxAsync("command", first.Owner, DateTimeOffset.UtcNow));
        await b.MarkOutboxPublishedAsync("command", "c", DateTimeOffset.UtcNow);
        Assert.Empty(await a.ClaimOutboxAsync("d", DateTimeOffset.UtcNow.AddMinutes(5), TimeSpan.FromMinutes(1), 1));
    }

    [PostgreSqlFact]
    public async Task InvocationAdmissionAndResponseShareOneTransactionAcrossConnections()
    {
        var store = OpenStore();
        var request = Request();
        var seed = new SagaInstance { Id = request.Identity.SourceSagaInstanceId };
        await store.SaveAsync(seed);
        await store.UpdateWithOutboxAsync(seed.Id, _ => (true, OutboxMessage.FromInvocation(request)));
        static OutboxMessage Response(IncomingInvocationDecision stored, string? id) => id is null
            ? OutboxMessage.FromInvocation(new ProcessInvocationRejected(stored.Reply, stored.RejectionReason!))
            : OutboxMessage.FromInvocation(new ProcessInvocationAccepted(stored.Reply, id));

        foreach (var reason in new string?[] { null, "Rejected" })
        {
            var collision = new IncomingInvocationDecision(request, reason, request.Message);
            await Assert.ThrowsAsync<DbUpdateException>(async () => await store.AdmitWithOutboxAsync(collision, Target, Response));
            Assert.Null(await store.FindIncomingDecisionAsync(request.Identity.InvocationId));
            Assert.Equal(seed.Id, Assert.Single(await store.ListAsync()).Id);
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            OpenStore().AdmitWithOutboxAsync(Decision(request, null), Target, Response).AsTask()));
        var first = results[0];
        Assert.All(results, result =>
        {
            Assert.Equal(first.TargetId, result.TargetId);
            Assert.Equal(first.Decision.Reply, result.Decision.Reply);
        });
        Assert.Equal(request.Identity, (await store.FindAsync(first.TargetId!))!.IncomingInvocation!.Identity);
        var conflicting = new ProcessInvocationRequest(request.Identity, Parse("""{"amount":2}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.AdmitWithOutboxAsync(Decision(conflicting, "Rejected"), Target, Response));
        var rejected = Request();
        var rejection = await store.AdmitWithOutboxAsync(Decision(rejected, "Rejected"),
            () => throw new InvalidOperationException("A rejection must not create a target"), Response);
        Assert.Null(rejection.TargetId);
        Assert.Equal(2, (await store.ListAsync()).Count);
        var rows = await store.ClaimOutboxAsync("inspect", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), 8);
        Assert.Equal(3, rows.Count);
        Assert.Equal(new ProcessInvocationAccepted(first.Decision.Reply, first.TargetId!),
            Assert.Single(rows, row => row.Message.MessageId == first.Decision.Reply.MessageId).Message.ToInvocation());
        Assert.Equal(new ProcessInvocationRejected(rejection.Decision.Reply, "Rejected"),
            Assert.Single(rows, row => row.Message.MessageId == rejection.Decision.Reply.MessageId).Message.ToInvocation());
    }

    [PostgreSqlFact]
    public async Task InvocationRequestAndTerminalStateRollBackWhenOutboxInsertFails()
    {
        var store = OpenStore();
        var request = Request();
        var source = new SagaInstance { Id = request.Identity.SourceSagaInstanceId, Status = SagaStatus.Running };
        await store.SaveAsync(source);
        await store.UpdateWithOutboxAsync(source.Id, state =>
        {
            state.CurrentStep = "invoke";
            return (true, OutboxMessage.FromInvocation(request));
        });
        await Assert.ThrowsAsync<DbUpdateException>(async () => await store.UpdateWithOutboxAsync(source.Id, state =>
        {
            state.CurrentStep = "changed";
            return (true, OutboxMessage.FromInvocation(request));
        }));
        Assert.Equal("invoke", (await store.FindAsync(source.Id))!.CurrentStep);

        var target = await store.GetOrCreateIncomingAsync(request, Target, Now);
        var collision = new ProcessInvocationTerminalOutcome(request.Message, target.Id, ExternalProcessStatus.Completed);
        async ValueTask Finish(ProcessInvocationTerminalOutcome response) => await store.UpdateWithOutboxAsync(target.Id, state =>
        {
            state.Status = SagaStatus.Completed;
            state.InvocationTerminalOutcome = response;
            state.InvocationTerminalQueued = true;
            return (true, OutboxMessage.FromInvocation(response));
        });
        await Assert.ThrowsAsync<DbUpdateException>(async () => await Finish(collision));
        var unchanged = (await store.FindAsync(target.Id))!;
        Assert.Equal(SagaStatus.Running, unchanged.Status);
        Assert.Null(unchanged.InvocationTerminalOutcome);
        Assert.False(unchanged.InvocationTerminalQueued);

        var terminal = new ProcessInvocationTerminalOutcome(
            new(request.Identity, Guid.NewGuid().ToString("N"), Now), target.Id, ExternalProcessStatus.Completed);
        await Finish(terminal);
        Assert.Equal(terminal, (await store.FindAsync(target.Id))!.InvocationTerminalOutcome);
        var rows = await OpenStore().ClaimOutboxAsync("new-dispatcher", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), 4);
        Assert.Equal(2, rows.Count);
        Assert.Equal(terminal, Assert.Single(rows, row => row.Message.MessageId == terminal.Message.MessageId).Message.ToInvocation());
    }

    [PostgreSqlFact]
    public async Task ExplicitTerminalCommitRejectsBypassesAndRollsBackFailedInsertBeforeConcurrentRetry()
    {
        var store = OpenStore();
        var target = await store.GetOrCreateIncomingAsync(Request(), Target, Now);
        await store.UpdateAsync(target.Id, state => { state.CurrentStep = "work"; return true; });
        var before = (await store.FindAsync(target.Id))!;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.UpdateAsync(target.Id,
            state => state.Status = SagaStatus.Failed));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.UpdateWithOutboxAsync(target.Id,
            state => (state.Status = SagaStatus.Failed, (OutboxMessage?)null)));
        var direct = (await store.FindAsync(target.Id))!;
        direct.Status = SagaStatus.Failed;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.SaveAsync(direct));

        var registry = new ProcessDefinitionRegistry();
        registry.RegisterLocalBatch([new SagaDefinition
        {
            Domain = target.DefinitionDomain, Name = target.DefinitionName, Version = target.DefinitionVersion,
            Steps = [new() { Name = "work", Command = "Work", Transitions = [new() { Event = "success", NextStep = "completed" }] }]
        }]);
        var bus = new UnavailableInvocationBus();
        var clock = new SystemSagaClock(TimeProvider.System);
        var dispatcher = new CommandDispatcher(invocationBus: bus);
        var publisher = new ProcessInvocationPublisher(store, bus, registry, clock, dispatcher);
        await PostgreSqlTestDatabase.ExecuteAsync(_database.ConnectionString, """
            ALTER TABLE sagaflow.outbox ADD CONSTRAINT test_reject_terminal
            CHECK (document->>'Name' <> 'ProcessInvocationTerminalOutcome')
            """);
        await Assert.ThrowsAsync<DbUpdateException>(() => publisher.TransitionTerminalAsync(target.Id, SagaStatus.Failed,
            state => state.FailureReason = "must roll back"));
        var unchanged = (await store.FindAsync(target.Id))!;
        Assert.Equal(before.Revision, unchanged.Revision);
        Assert.Equal(SagaStatus.Running, unchanged.Status);
        Assert.Equal("work", unchanged.CurrentStep);
        Assert.Null(unchanged.FailureReason);
        Assert.Null(unchanged.InvocationTerminalOutcome);
        Assert.False(unchanged.InvocationTerminalQueued);
        Assert.Empty(await store.ClaimOutboxAsync("check", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), 2));

        await PostgreSqlTestDatabase.ExecuteAsync(_database.ConnectionString,
            "ALTER TABLE sagaflow.outbox DROP CONSTRAINT test_reject_terminal");
        var mutations = 0;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            new ProcessInvocationPublisher(OpenStore(), bus, registry, clock, dispatcher)
                .TransitionTerminalAsync(target.Id, SagaStatus.Failed, state =>
                {
                    Interlocked.Increment(ref mutations);
                    state.FailureReason = "explicit failure";
                })));
        var terminal = (await store.FindAsync(target.Id))!;
        Assert.Equal(before.Revision + 8, terminal.Revision);
        Assert.Equal(1, mutations);
        Assert.Equal(SagaStatus.Failed, terminal.Status);
        Assert.Null(terminal.CurrentStep);
        Assert.Equal("explicit failure", terminal.FailureReason);
        Assert.True(terminal.InvocationTerminalQueued);
        Assert.Equal(ExternalProcessStatus.Failed, terminal.InvocationTerminalOutcome!.Status);
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var query = new NpgsqlCommand("SELECT message_id, published_at FROM sagaflow.outbox", connection);
        await using var rows = await query.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync());
        Assert.Equal(terminal.InvocationTerminalOutcome.Message.MessageId, rows.GetString(0));
        Assert.True(rows.IsDBNull(1));
        Assert.False(await rows.ReadAsync());
    }

    private sealed class UnavailableInvocationBus : IProcessInvocationBus
    {
        public ValueTask PublishOutboxAsync(OutboxMessage message, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("Broker unavailable"));
        public ValueTask PublishAsync(ProcessInvocationRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask PublishAsync(ProcessInvocationAccepted accepted, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask PublishAsync(ProcessInvocationRejected rejected, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask PublishAsync(ProcessInvocationTerminalOutcome outcome, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    [PostgreSqlFact]
    public async Task ExistingVersionOneSchemaMigratesWithoutReplacingSagaRows()
    {
        await using var legacy = new PostgreSqlTestDatabase();
        await legacy.CreateAsync();
        await PostgreSqlTestDatabase.ExecuteAsync(legacy.ConnectionString, "CREATE SCHEMA sagaflow; CREATE TABLE sagaflow.schema_version (singleton boolean PRIMARY KEY CHECK (singleton), version integer NOT NULL CHECK (version > 0)); INSERT INTO sagaflow.schema_version VALUES (true, 1)");
        using (var script = typeof(PostgreSqlStorageSchema).Assembly.GetManifestResourceStream("SagaFlow.PostgreSQL.V001.sql")!)
        using (var reader = new StreamReader(script))
            await PostgreSqlTestDatabase.ExecuteAsync(legacy.ConnectionString, await reader.ReadToEndAsync());
        var store = new PostgreSqlSagaStateStore(legacy.ConnectionString);
        var state = Target();
        await store.SaveAsync(state);
        await PostgreSqlStorageSchema.InitializeAsync(legacy.ConnectionString);
        Assert.Equal(1, (await store.FindAsync(state.Id))!.Revision);
        Assert.Empty(await store.ClaimOutboxAsync("owner", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), 1));
    }

    [PostgreSqlFact]
    public async Task ConcurrentDispatchersRespectLiveLeaseAndRecoverExpiredPublication()
    {
        var store = OpenStore();
        var state = Target();
        await store.SaveAsync(state);
        var created = DateTimeOffset.UtcNow.AddMinutes(-1);
        var message = new OutboxMessage
        {
            MessageId = "stable", Name = "Work", Destination = "work", Body = [123, 125], RecoverySnapshot = [1],
            Headers = new() { ProtocolVersion = 1, SagaInstanceId = state.Id, StepId = "work", CommandId = "stable",
                CommandName = "Work", ExecutionId = "stable-execution", CreatedAt = created }
        };
        await store.UpdateWithOutboxAsync(state.Id, working => { working.CurrentStep = "work"; return (true, message); });
        var confirmFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstBus = new OutboxRecordingBus(confirmFirst.Task);
        var nextBus = new OutboxRecordingBus();
        var first = new CommandDispatcher(firstBus);
        var next = new CommandDispatcher(nextBus);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var inFlight = first.DispatchOutboxAsync(OpenStore(), deadline.Token);
        try
        {
            await firstBus.Published.Task.WaitAsync(deadline.Token);
            await next.DispatchOutboxAsync(OpenStore(), deadline.Token);
            Assert.Empty(nextBus.Commands);
            // Simulate a lease expiring while the first dispatcher still waits for confirmation.
            await PostgreSqlTestDatabase.ExecuteAsync(_database.ConnectionString, """
                UPDATE sagaflow.outbox SET lease_until = clock_timestamp() - interval '1 second'
                WHERE message_id = 'stable'
                """);
            await next.DispatchOutboxAsync(OpenStore(), deadline.Token);
        }
        finally
        {
            confirmFirst.TrySetResult();
            await inFlight;
        }

        var original = Assert.Single(firstBus.Commands);
        var replay = Assert.Single(nextBus.Commands);
        Assert.Equal(original.Headers, replay.Headers);
        Assert.Equal(original.RecoverySnapshot, replay.RecoverySnapshot);
        Assert.All(new[] { original, replay }, command =>
        {
            Assert.Equal("stable", command.Headers.CommandId);
            Assert.Equal("stable-execution", command.Headers.ExecutionId);
            Assert.Equal("{}", command.Payload.GetRawText());
        });
        Assert.Empty(await OpenStore().ClaimOutboxAsync("later", DateTimeOffset.UtcNow.AddMinutes(5), TimeSpan.FromMinutes(1), 1));
    }

    [PostgreSqlFact]
    public async Task NewHostDispatchesCommittedCommandAfterPreviousHostStoppedBeforePublish()
    {
        const string preparationOwner = "preparation";
        var store = OpenStore();
        var state = Target();
        await store.SaveAsync(state);
        var message = new OutboxMessage
        {
            MessageId = "restart-command", Name = "Work", Destination = "work", Body = [123, 125], RecoverySnapshot = [1],
            PreparationOwner = preparationOwner,
            Headers = new() { ProtocolVersion = 1, SagaInstanceId = state.Id, StepId = "work", CommandId = "restart-command",
                CommandName = "Work", ExecutionId = "restart-execution", CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1) }
        };
        await store.UpdateWithOutboxAsync(state.Id, working => { working.CurrentStep = "work"; return (true, message); });
        Assert.Empty(await OpenStore().ClaimOutboxAsync("another-dispatcher", DateTimeOffset.UtcNow.AddMinutes(5),
            TimeSpan.FromMinutes(1), 1));
        await store.ReleaseOutboxAsync(message.MessageId, preparationOwner, DateTimeOffset.UtcNow);
        var bus = new OutboxRecordingBus();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSagaFlow(options => options.Storage.UsePostgreSql(storage =>
            storage.ConnectionString = _database.ConnectionString));
        builder.Services.AddSingleton<ISagaMessageBus>(bus);
        using var restarted = builder.Build();
        await restarted.StartAsync();
        await bus.Published.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using (var connection = new NpgsqlConnection(_database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var check = new NpgsqlCommand("SELECT published_at IS NOT NULL FROM sagaflow.outbox WHERE message_id = 'restart-command'", connection);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!((bool?)await check.ExecuteScalarAsync(deadline.Token) ?? false))
                await Task.Delay(50, deadline.Token);
        }
        await restarted.StopAsync();
        var command = Assert.Single(bus.Commands);
        Assert.Equal(message.MessageId, command.Headers.CommandId);
        Assert.Equal(message.Headers.ExecutionId, command.Headers.ExecutionId);
        Assert.Empty(await store.ClaimOutboxAsync("later", DateTimeOffset.UtcNow.AddMinutes(5), TimeSpan.FromMinutes(1), 1));
    }

    private sealed class OutboxRecordingBus(Task? confirmation = null) : ISagaMessageBus
    {
        public List<SagaCommand> Commands { get; } = [];
        public TaskCompletionSource Published { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask PublishOutboxAsync(OutboxMessage message, CancellationToken ct = default) =>
            PublishAsync(message.ToCommand(), ct);
        public ValueTask PublishAsync(SagaCommand command, CancellationToken ct = default)
        {
            Commands.Add(command);
            Published.TrySetResult();
            return confirmation is null ? ValueTask.CompletedTask : new ValueTask(confirmation.WaitAsync(ct));
        }
    }

    public async Task InitializeAsync()
    {
        await _database.CreateAsync();
        await PostgreSqlStorageSchema.InitializeAsync(_database.ConnectionString);
    }

    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

    [PostgreSqlFact]
    public async Task RichAggregateRoundTripsWithExactPayloadsAndIsolatedSnapshots()
    {
        var incoming = Request();
        var outgoing = Request();
        var payload = Parse("""{ "b": 1e0, "a": "\u0061", "a": null }""");
        var candidate = Target();
        candidate.Context = payload;
        candidate.CurrentStep = "work";
        candidate.PayloadType = typeof(PostgreSqlStoreTests).AssemblyQualifiedName;
        candidate.FailureReason = "retained detail";
        var headers = new SagaMessageHeaders
        {
            ProtocolVersion = 1, ProcessDomain = "target", ProcessName = "remote", ProcessVersion = "1.0",
            ExecutionId = "exec", CommandId = "command-id", CommandName = "Work", CausationId = "cause",
            ForwardExecutionId = "forward", ReplyDestination = "target.outcomes", CreatedAt = Now,
            SagaInstanceId = candidate.Id, CorrelationId = candidate.CorrelationId, StepId = "work"
        };
        var expected = new SagaInstance
        {
            Id = candidate.Id, DefinitionDomain = candidate.DefinitionDomain, DefinitionName = candidate.DefinitionName,
            DefinitionVersion = candidate.DefinitionVersion, CorrelationId = candidate.CorrelationId, Status = candidate.Status,
            Context = candidate.Context, CurrentStep = candidate.CurrentStep, PayloadType = candidate.PayloadType,
            CreatedAt = Now, UpdatedAt = Now, FailureReason = candidate.FailureReason,
            Steps = [new()
            {
                StepName = "work", Command = "Work", External = true, Status = SagaStepStatus.TimedOut, ExecutionOrder = 1,
                Execution = headers, Input = payload, Result = Parse("null"), StartedAt = Now, CompletedAt = Now.AddSeconds(2),
                TimeoutLimit = TimeSpan.FromSeconds(2), TimedOutAt = Now.AddSeconds(2), Invocation = outgoing,
                ObservedMessages = [new("observed", "fingerprint")], Reconciliation = new("ambiguous publication", Now),
                Publication = new("attempt", Now, "error"), InvocationProgress = new(ExternalProcessStatus.Rejected,
                    null, Now, Now.AddSeconds(1), Now.AddSeconds(2), "denied")
            }],
            Compensations = [new()
            {
                StepId = "work", Command = "Undo", ExecutionOrder = 1, Execution = headers with { IsCompensation = true },
                Status = SagaCompensationStatus.Completed, StartedAt = Now, CompletedAt = Now.AddSeconds(3), Result = payload
            }],
            DomainRollback = new() { Status = SagaDomainRollbackStatus.Failed, StartedAt = Now, CompletedAt = Now.AddSeconds(4), Error = "rollback failed" }
        };

        var store = OpenStore();
        await store.GetOrCreateIncomingAsync(incoming, () => expected, Now);
        Assert.Equal(0, expected.Revision);
        Assert.Null(expected.IncomingInvocation);
        var terminal = new ProcessInvocationTerminalOutcome(
            new(incoming.Identity, Guid.NewGuid().ToString("N"), Now, incoming.Message.MessageId),
            candidate.Id, ExternalProcessStatus.Compensated);
        await store.UpdateWithOutboxAsync(expected.Id, working =>
        {
            working.Status = SagaStatus.Compensated;
            working.CurrentStep = null;
            working.InvocationTerminalOutcome = terminal;
            working.InvocationTerminalQueued = true;
            return (true, OutboxMessage.FromInvocation(terminal));
        });
        expected.Revision = 2;
        expected.Status = SagaStatus.Compensated;
        expected.CurrentStep = null;
        expected.InvocationTerminalOutcome = terminal;
        expected.InvocationTerminalQueued = true;
        expected.UpdatedAt = Now.ToUniversalTime();
        expected.IncomingInvocation = new(incoming.Identity, incoming.Message.OccurredAt, Now.ToUniversalTime(),
            incoming.Payload, incoming.Message);
        var saved = Assert.IsType<SagaInstance>(await OpenStore().FindAsync(expected.Id));
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(saved));
        Assert.Equal(Now.Offset, saved.CreatedAt.Offset);
        Assert.Equal(payload.GetRawText(), saved.Context.GetRawText());
        Assert.Equal(payload.GetRawText(), saved.Steps[0].Input!.Value.GetRawText());
        Assert.Equal(JsonValueKind.Null, saved.Steps[0].Result!.Value.ValueKind);
        Assert.Equal(incoming.Payload.GetRawText(), saved.IncomingInvocation!.Payload!.Value.GetRawText());
        Assert.Equal(outgoing.Payload.GetRawText(), saved.Steps[0].Invocation!.Payload.GetRawText());

        saved.Steps[0].Status = SagaStepStatus.Failed;
        var result = await store.UpdateAsync(saved.Id, working => working);
        Assert.Equal(SagaStepStatus.TimedOut, result.Steps[0].Status);
        result.Compensations[0].Status = SagaCompensationStatus.Pending;
        Assert.Equal(SagaCompensationStatus.Completed, (await store.FindAsync(saved.Id))!.Compensations[0].Status);
    }

    [PostgreSqlFact]
    public async Task SaveAndListRespectRevisionsMissingIdsAndOrdinalOrdering()
    {
        var store = OpenStore();
        var first = new SagaInstance { Id = "first", CreatedAt = Now };
        var second = new SagaInstance { Id = "second", CreatedAt = Now };
        var newer = new SagaInstance { Id = "newer", CreatedAt = Now.AddTicks(1) };
        Assert.Null(await store.FindAsync(first.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(async () => await store.UpdateAsync(first.Id, _ => true));
        await store.SaveAsync(second);
        await store.SaveAsync(first);
        await store.SaveAsync(newer);
        Assert.Equal(0, first.Revision);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await OpenStore().SaveAsync(first));

        var current = (await store.FindAsync(first.Id))!;
        var stale = (await OpenStore().FindAsync(first.Id))!;
        current.Status = SagaStatus.Running;
        await store.SaveAsync(current);
        stale.Status = SagaStatus.Failed;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await OpenStore().SaveAsync(stale));
        Assert.Contains("Stale Saga state revision", error.Message);
        var list = await store.ListAsync();
        Assert.Equal(["newer", "first", "second"], list.Select(x => x.Id));
        Assert.Equal(2, list[1].Revision);
        Assert.Equal(SagaStatus.Running, list[1].Status);
        list[1].Status = SagaStatus.Failed;
        Assert.Equal(SagaStatus.Running, (await store.FindAsync(first.Id))!.Status);
    }

    [PostgreSqlFact]
    public async Task IndependentStoresSerializeConcurrentUpdates()
    {
        var first = OpenStore();
        var second = OpenStore();
        var state = new SagaInstance { Context = JsonSerializer.SerializeToElement(0) };
        await first.SaveAsync(state);
        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(async i =>
            await (i % 2 == 0 ? first : second).UpdateAsync(state.Id, working =>
            {
                var next = working.Context.GetInt32() + 1;
                working.Context = JsonSerializer.SerializeToElement(next);
                return next;
            })));
        Assert.Equal(Enumerable.Range(1, 40), results.Order());
        var final = (await second.FindAsync(state.Id))!;
        Assert.Equal(41, final.Revision);
        Assert.Equal(40, final.Context.GetInt32());
    }

    [PostgreSqlFact]
    public async Task CallbackFailureCancellationAndReentrancyRollBack()
    {
        var store = OpenStore();
        var state = new SagaInstance();
        await store.SaveAsync(state);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.UpdateAsync<bool>(state.Id, working =>
        {
            working.Status = SagaStatus.Failed;
            throw new InvalidOperationException("abort");
        }));
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.UpdateAsync(state.Id, working =>
        {
            working.Status = SagaStatus.Failed;
            cancellation.Cancel();
            return true;
        }, cancellation.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.UpdateAsync(state.Id, _ =>
            OpenStore().UpdateAsync(state.Id, _ => true).GetAwaiter().GetResult()));
        var unchanged = (await OpenStore().FindAsync(state.Id))!;
        Assert.Equal(SagaStatus.Pending, unchanged.Status);
        Assert.Equal(1, unchanged.Revision);
    }

    [PostgreSqlFact]
    public async Task CancellationWhileWaitingForAnotherSessionDoesNotRunTheCallback()
    {
        var store = OpenStore();
        var state = new SagaInstance();
        await store.SaveAsync(state);

        await using var blocker = new NpgsqlConnection(_database.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using var rowLock = new NpgsqlCommand("SELECT id FROM sagaflow.instances WHERE id = @id FOR UPDATE", blocker, transaction);
        rowLock.Parameters.AddWithValue("id", state.Id);
        Assert.Equal(state.Id, await rowLock.ExecuteScalarAsync());

        using var cancellation = new CancellationTokenSource();
        var settings = new NpgsqlConnectionStringBuilder(_database.ConnectionString) { ApplicationName = "sagaflow-lock-wait-test" };
        var other = new PostgreSqlSagaStateStore(settings.ConnectionString);
        var waiting = other.UpdateAsync<bool>(state.Id, _ => throw new InvalidOperationException("The callback must not run."),
            cancellation.Token).AsTask();
        await using var observer = new NpgsqlConnection(_database.ConnectionString);
        await observer.OpenAsync();
        await using var waitingSessions = new NpgsqlCommand("""
            SELECT count(*) FROM pg_stat_activity
            WHERE datname = current_database()
              AND application_name = 'sagaflow-lock-wait-test' AND wait_event_type = 'Lock'
            """, observer);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while ((long)(await waitingSessions.ExecuteScalarAsync(deadline.Token))! == 0)
            await Task.Delay(20, deadline.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        await transaction.RollbackAsync();
        Assert.Equal(1, (await store.FindAsync(state.Id))!.Revision);
    }

    [PostgreSqlFact]
    public async Task IndependentStoresCreateOneClaimAndPreserveRawDuplicateRules()
    {
        var first = OpenStore();
        var second = OpenStore();
        var request = Request();
        var calls = 0;
        var claims = await Task.WhenAll(Enumerable.Range(0, 24).Select(async i =>
            await (i % 2 == 0 ? first : second).GetOrCreateIncomingAsync(request, () =>
            {
                Interlocked.Increment(ref calls);
                return Target();
            }, Now)));
        var id = Assert.Single(claims.Select(claim => claim.Id).Distinct());
        Assert.Equal(1, calls);
        var saved = Assert.Single(await second.ListAsync());
        Assert.Equal(id, saved.Id);
        Assert.Equal(request.Identity, saved.IncomingInvocation!.Identity);
        Assert.NotEqual(request.Identity.CorrelationId, saved.CorrelationId);
        var differentSpelling = new ProcessInvocationRequest(request.Identity, Parse("""{"amount":1}"""), request.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await second.GetOrCreateIncomingAsync(differentSpelling, Target, Now));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await second.UpdateAsync(id, state =>
        {
            state.IncomingInvocation = null;
            return true;
        }));
        await second.SaveAsync(saved);
        Assert.Equal(2, (await first.GetOrCreateIncomingAsync(request, () => throw new InvalidOperationException("duplicate factory"), Now)).Revision);
    }

    [PostgreSqlFact]
    public async Task AttachPreservesLocalIdentityAndRejectsRebindingOrASecondClaim()
    {
        var first = OpenStore();
        var second = OpenStore();
        var target = Target();
        var other = Target();
        await first.SaveAsync(target);
        await first.SaveAsync(other);
        var request = Request();
        var attached = await second.AttachIncomingAsync(target.Id, request, Now);
        Assert.Equal(target.CorrelationId, attached.CorrelationId);
        Assert.Equal(2, attached.Revision);
        Assert.Equal(2, (await first.AttachIncomingAsync(target.Id, request, Now)).Revision);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await first.AttachIncomingAsync(other.Id, request, Now));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await first.AttachIncomingAsync(target.Id, Request(), Now));
        Assert.Null((await second.FindAsync(other.Id))!.IncomingInvocation);
    }

    [PostgreSqlFact]
    public async Task AdmissionDecisionsAreFirstWinsAndPersistAcrossHosts()
    {
        var request = Request();
        var rejected = Decision(request, "denied");
        var otherDecision = Decision(request, "another reason");
        var instance = Target();
        ISagaStateStore first;
        IncomingInvocationDecision[] results;
        using (var firstHost = CreateHost(storageFirst: true))
        {
            await firstHost.StartAsync();
            first = firstHost.Services.GetRequiredService<ISagaStateStore>();
            using var scope = firstHost.Services.CreateScope();
            Assert.Same(first, scope.ServiceProvider.GetRequiredService<ISagaStateStore>());
            var second = OpenStore();
            results = await Task.WhenAll(first.RecordIncomingDecisionAsync(rejected).AsTask(),
                second.RecordIncomingDecisionAsync(otherDecision).AsTask());
            Assert.Equal(results[0].Reply, results[1].Reply);
            Assert.Equal(results[0].RejectionReason, results[1].RejectionReason);
            await first.SaveAsync(instance);
            await firstHost.StopAsync();
        }

        await PostgreSqlStorageSchema.InitializeAsync(_database.ConnectionString);
        using var freshHost = CreateHost(storageFirst: false);
        await freshHost.StartAsync();
        var fresh = freshHost.Services.GetRequiredService<ISagaStateStore>();
        Assert.NotSame(first, fresh);
        Assert.Equal(instance.Id, (await fresh.FindAsync(instance.Id))!.Id);
        var persisted = (await fresh.FindIncomingDecisionAsync(Guid.Parse(request.Identity.InvocationId).ToString("D")))!;
        Assert.Equal(results[0].Reply, persisted.Reply);
        Assert.Equal(request.Message, persisted.Request.Message);
        Assert.Equal(request.Payload.GetRawText(), persisted.Request.Payload.GetRawText());
        Assert.Equal(results[0].RejectionReason, persisted.RejectionReason);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await fresh.GetOrCreateIncomingAsync(request, Target, Now));
        var conflict = new ProcessInvocationRequest(request.Identity, Parse("null"), request.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await fresh.RecordIncomingDecisionAsync(Decision(conflict, null)));
        await freshHost.StopAsync();
    }

    [PostgreSqlFact]
    public async Task AClaimAndARejectionCannotBothWin()
    {
        var request = Request();
        var first = OpenStore();
        var second = OpenStore();
        var claim = TryClaim();
        var reject = TryReject();
        await Task.WhenAll(claim, reject);
        Assert.NotEqual(await claim, await reject);
        var decision = await first.FindIncomingDecisionAsync(request.Identity.InvocationId);
        var instances = await second.ListAsync();
        if (await claim)
        {
            Assert.Null(decision);
            Assert.Equal(request.Identity, Assert.Single(instances).IncomingInvocation!.Identity);
            var accepted = Decision(request, null);
            await first.RecordIncomingDecisionAsync(accepted);
            Assert.Equal(accepted.Reply, (await second.FindIncomingDecisionAsync(request.Identity.InvocationId))!.Reply);
        }
        else
        {
            Assert.Equal("denied", decision!.RejectionReason);
            Assert.Empty(instances);
        }

        async Task<bool> TryClaim()
        {
            try { await first.GetOrCreateIncomingAsync(request, Target, Now); return true; }
            catch (InvalidOperationException error) when (error.Message == "Invocation was rejected.") { return false; }
        }

        async Task<bool> TryReject()
        {
            try { await second.RecordIncomingDecisionAsync(Decision(request, "denied")); return true; }
            catch (InvalidOperationException error) when (error.Message == "Admission conflicts with an existing target.") { return false; }
        }
    }

    [PostgreSqlFact]
    public async Task FailedAndCanceledClaimsRollBackBothRows()
    {
        var request = Request();
        var store = OpenStore();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.GetOrCreateIncomingAsync(request,
            () => throw new InvalidOperationException("factory failed"), Now));
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.GetOrCreateIncomingAsync(request, () =>
        {
            cancellation.Cancel();
            return Target();
        }, Now, cancellation.Token));

        await PostgreSqlTestDatabase.ExecuteAsync(_database.ConnectionString, """
            CREATE FUNCTION sagaflow.fail_claim() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'forced claim failure';
            END;
            $$;
            CREATE TRIGGER fail_claim BEFORE UPDATE OF target_id ON sagaflow.incoming_invocations
                FOR EACH ROW EXECUTE FUNCTION sagaflow.fail_claim();
            """);
        var error = await Assert.ThrowsAsync<DbUpdateException>(async () => await store.GetOrCreateIncomingAsync(request, Target, Now));
        Assert.Contains("forced claim failure", Assert.IsType<PostgresException>(error.InnerException).MessageText);
        Assert.Empty(await OpenStore().ListAsync());
        await using (var connection = new NpgsqlConnection(_database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var count = new NpgsqlCommand("SELECT count(*) FROM sagaflow.incoming_invocations", connection);
            Assert.Equal(0L, await count.ExecuteScalarAsync());
        }
        await PostgreSqlTestDatabase.ExecuteAsync(_database.ConnectionString, "DROP TRIGGER fail_claim ON sagaflow.incoming_invocations");
        Assert.NotNull(await OpenStore().GetOrCreateIncomingAsync(request, Target, Now));
    }

    [PostgreSqlFact]
    public async Task SchemaInitializationIsConcurrentAndRejectsUnsupportedVersions()
    {
        await using var empty = new PostgreSqlTestDatabase();
        await empty.CreateAsync();
        await Task.WhenAll(PostgreSqlStorageSchema.InitializeAsync(empty.ConnectionString),
            PostgreSqlStorageSchema.InitializeAsync(empty.ConnectionString));
        var store = new PostgreSqlSagaStateStore(empty.ConnectionString);
        var instance = new SagaInstance();
        await store.SaveAsync(instance);
        await PostgreSqlStorageSchema.InitializeAsync(empty.ConnectionString);
        Assert.NotNull(await store.FindAsync(instance.Id));
        await PostgreSqlTestDatabase.ExecuteAsync(empty.ConnectionString, "UPDATE sagaflow.schema_version SET version = 3");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => PostgreSqlStorageSchema.InitializeAsync(empty.ConnectionString));
        Assert.Contains("schema version 3", error.Message);
        Assert.NotNull(await store.FindAsync(instance.Id));
    }

    [PostgreSqlFact]
    public async Task MissingDatabaseSurfacesTheServerError()
    {
        var settings = new NpgsqlConnectionStringBuilder(_database.ConnectionString) { Database = $"missing_{Guid.NewGuid():N}" };
        var store = new PostgreSqlSagaStateStore(settings.ConnectionString);
        var error = await Assert.ThrowsAsync<PostgresException>(async () => await store.ListAsync());
        Assert.Equal(PostgresErrorCodes.InvalidCatalogName, error.SqlState);
        var initializationError = await Assert.ThrowsAsync<PostgresException>(() =>
            PostgreSqlStorageSchema.InitializeAsync(settings.ConnectionString));
        Assert.Equal(PostgresErrorCodes.InvalidCatalogName, initializationError.SqlState);
    }

    [PostgreSqlFact]
    public async Task UnifiedSelectionInitializesBeforeOtherServicesAndPreservesStateAcrossStarts()
    {
        await using var empty = new PostgreSqlTestDatabase();
        await empty.CreateAsync();
        var instance = Target();

        IHost BuildHost()
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            builder.Services.Configure<HostOptions>(options => options.ServicesStartConcurrently = true);
            // Register the probe first so this test also checks startup phase ordering.
            builder.Services.AddHostedService<StoreStartupProbe>();
            builder.Services.AddSagaFlow(options => options.Storage.UsePostgreSql(storage =>
                storage.ConnectionString = empty.ConnectionString));
            return builder.Build();
        }

        using (var host = BuildHost())
        {
            await host.StartAsync();
            var store = Assert.IsType<PostgreSqlSagaStateStore>(host.Services.GetRequiredService<ISagaStateStore>());
            using var scope = host.Services.CreateScope();
            Assert.Same(store, scope.ServiceProvider.GetRequiredService<ISagaStateStore>());
            await store.SaveAsync(instance);
            await host.StopAsync();
        }

        using (var host = BuildHost())
        {
            await host.StartAsync();
            Assert.NotNull(await host.Services.GetRequiredService<ISagaStateStore>().FindAsync(instance.Id));
            await host.StopAsync();
        }

        await PostgreSqlTestDatabase.ExecuteAsync(empty.ConnectionString, "UPDATE sagaflow.schema_version SET version = 3");
        using var incompatibleHost = BuildHost();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => incompatibleHost.StartAsync());
        Assert.Contains("schema version 3", error.InnerException!.Message);
        Assert.NotNull(await incompatibleHost.Services.GetRequiredService<ISagaStateStore>().FindAsync(instance.Id));
    }

    private sealed class StoreStartupProbe(ISagaStateStore store) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken) => await store.ListAsync(cancellationToken);
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid connection string")]
    public void UnifiedSelectionRejectsInvalidConnectionSettings(string? connection)
    {
        var services = new ServiceCollection();
        var error = Record.Exception(() => services.AddSagaFlow(options =>
            options.Storage.UsePostgreSql(storage => storage.ConnectionString = connection)));
        Assert.NotNull(error);
        Assert.Contains("PostgreSQL", error.Message);
    }

    private IHost CreateHost(bool storageFirst)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        if (storageFirst) builder.Services.AddSagaFlowPostgreSqlStorage(_database.ConnectionString);
        builder.Services.AddSagaFlow();
        if (!storageFirst) builder.Services.AddSagaFlowPostgreSqlStorage(_database.ConnectionString);
        return builder.Build();
    }

    private static SagaInstance Target() => new()
    {
        DefinitionDomain = "target", DefinitionName = "remote", DefinitionVersion = "1.0",
        CorrelationId = "target-correlation", CreatedAt = Now, UpdatedAt = Now, Status = SagaStatus.Running
    };

    private static ProcessInvocationRequest Request()
    {
        var identity = new ProcessInvocationIdentity(Guid.NewGuid().ToString("N"), "source-correlation",
            new("source", "process", "1.0"), "source-saga", "invoke", new("target", "remote", "1.0"), "source-execution");
        return new(identity, Parse("""{ "amount": 1e0 }"""), new(identity, Guid.NewGuid().ToString("N"), Now, "request-cause"));
    }

    private static IncomingInvocationDecision Decision(ProcessInvocationRequest request, string? rejection) =>
        new(request, rejection, new(request.Identity, Guid.NewGuid().ToString("N"), Now, request.Message.MessageId));

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
