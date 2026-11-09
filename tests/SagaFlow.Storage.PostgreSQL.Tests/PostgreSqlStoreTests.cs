using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Messages;
using SagaFlow.Core.State;
using SagaFlow.Hosting.DependencyInjection;
using SagaFlow.Storage.PostgreSQL.DependencyInjection;
using SagaFlow.Storage.PostgreSQL.Schema;

namespace SagaFlow.Storage.PostgreSQL.Tests;

public sealed class PostgreSqlStoreTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.FromHours(3)).AddTicks(7);
    private readonly PostgreSqlTestDatabase _database = new();
    private ISagaStateStore OpenStore() => new PostgreSqlSagaStateStore(_database.ConnectionString);

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
            DomainRollback = new() { Status = SagaDomainRollbackStatus.Failed, StartedAt = Now, CompletedAt = Now.AddSeconds(4), Error = "rollback failed" },
            InvocationTerminalOutcome = new(new(incoming.Identity, Guid.NewGuid().ToString("N"), Now, incoming.Message.MessageId),
                candidate.Id, ExternalProcessStatus.Compensated)
        };

        var store = OpenStore();
        await store.GetOrCreateIncomingAsync(incoming, () => expected, Now);
        Assert.Equal(0, expected.Revision);
        Assert.Null(expected.IncomingInvocation);
        expected.Revision = 1;
        expected.UpdatedAt = Now.ToUniversalTime();
        expected.IncomingInvocation = new(incoming.Identity, incoming.Message.OccurredAt, Now.ToUniversalTime(), incoming.Payload);
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
        await PostgreSqlTestDatabase.ExecuteAsync(empty.ConnectionString, "UPDATE sagaflow.schema_version SET version = 2");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => PostgreSqlStorageSchema.InitializeAsync(empty.ConnectionString));
        Assert.Contains("schema version 2", error.Message);
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
