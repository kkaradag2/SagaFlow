using System.Data;
using Microsoft.EntityFrameworkCore;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Messages;
using SagaFlow.Core.State;
using SagaFlow.Storage.PostgreSQL.Models;
using SagaFlow.Storage.PostgreSQL.Persistence;

namespace SagaFlow.Storage.PostgreSQL;

/// <summary>Persists saga snapshots and invocation state in a pre-initialized PostgreSQL database.</summary>
public sealed class PostgreSqlSagaStateStore : ISagaStateStore
{
    private readonly StorageDatabase _database;
    private readonly IncomingInvocationStore _incoming;

    /// <summary>Validates connection settings. Each operation opens its own EF Core context.</summary>
    public PostgreSqlSagaStateStore(string connectionString)
    {
        _database = new StorageDatabase(connectionString);
        _incoming = new IncomingInvocationStore(_database);
    }

    public async ValueTask<TResult> UpdateWithOutboxAsync<TResult>(string instanceId,
        Func<SagaInstance, (TResult Result, OutboxMessage? Message)> update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await using var context = _database.Open(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var row = await context.LockInstanceAsync(instanceId, cancellationToken)
            ?? throw new KeyNotFoundException($"Saga instance '{instanceId}' was not found.");
        var working = row.Read();
        var incoming = working.IncomingInvocation;
        var terminal = IncomingTerminalWrite.Capture(working);
        var (result, message) = StorageDatabase.InvokeCallback(() => update(working));
        cancellationToken.ThrowIfCancellationRequested();
        EnsureIncomingUnchanged(incoming, working.IncomingInvocation);
        terminal.Validate(working, message);
        if (message is not null)
        {
            message.ValidateFor(instanceId);
            context.Outbox.Add(OutboxRow.Create(message));
        }
        row.Write(working);
        await context.SaveChangesAsync(cancellationToken);
        await StorageDatabase.CommitAsync(transaction, cancellationToken);
        return result;
    }

    public async ValueTask<IReadOnlyList<OutboxClaim>> ClaimOutboxAsync(string owner, DateTimeOffset now, TimeSpan lease,
        int limit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        if (lease <= TimeSpan.Zero || limit <= 0) throw new ArgumentOutOfRangeException(nameof(lease));
        await using var context = _database.Open(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var databaseNow = await context.Database.SqlQueryRaw<DateTimeOffset>(
            """SELECT transaction_timestamp() AS "Value" """).SingleAsync(cancellationToken);
        var rows = await context.Outbox.FromSql($"""
            SELECT * FROM sagaflow.outbox
            WHERE published_at IS NULL AND retry_at <= {databaseNow}
              AND (lease_until IS NULL OR lease_until <= {databaseNow})
            ORDER BY retry_at, created_at
            LIMIT {limit} FOR UPDATE SKIP LOCKED
            """).ToListAsync(cancellationToken);
        var until = databaseNow + lease;
        foreach (var row in rows)
        {
            row.Owner = owner;
            row.LeaseUntil = until;
            row.Attempts++;
        }
        await context.SaveChangesAsync(cancellationToken);
        await StorageDatabase.CommitAsync(transaction, cancellationToken);
        return rows.Select(row => new OutboxClaim(row.Read(), owner, row.Attempts, until)).ToArray();
    }

    public ValueTask<(IncomingInvocationDecision Decision, string? TargetId)> AdmitWithOutboxAsync(
        IncomingInvocationDecision decision, Func<SagaInstance> create, Func<IncomingInvocationDecision, string?, OutboxMessage> prepare,
        CancellationToken cancellationToken = default) =>
        _incoming.AdmitWithOutboxAsync(decision, create, prepare, cancellationToken);

    public async ValueTask MarkOutboxPublishedAsync(string messageId, string owner, DateTimeOffset publishedAt,
        CancellationToken cancellationToken = default)
    {
        await using var context = _database.Open(cancellationToken);
        var updated = await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE sagaflow.outbox SET published_at = {publishedAt}, owner = NULL, lease_until = NULL
            WHERE message_id = {messageId} AND owner = {owner} AND published_at IS NULL
            """, cancellationToken);
        if (updated != 1) throw new OutboxClaimLostException();
    }

    public async ValueTask ReleaseOutboxAsync(string messageId, string owner, DateTimeOffset retryAt,
        CancellationToken cancellationToken = default)
    {
        await using var context = _database.Open(cancellationToken);
        var updated = await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE sagaflow.outbox SET retry_at = {retryAt}, owner = NULL, lease_until = NULL
            WHERE message_id = {messageId} AND owner = {owner} AND published_at IS NULL
            """, cancellationToken);
        if (updated != 1) throw new OutboxClaimLostException();
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryRecoverIncomingAsync(SagaInstance instance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var incoming = instance.IncomingInvocation ?? throw new InvalidOperationException("Incoming recovery requires provenance.");
        if (instance.Revision != 0 || incoming.RequestMessage?.Identity != incoming.Identity || incoming.Payload is null ||
            incoming.RequestMessage.OccurredAt != incoming.CreatedAt ||
            new ProcessIdentity(instance.DefinitionDomain, instance.DefinitionName, instance.DefinitionVersion) != incoming.Identity.Target)
            throw new InvalidOperationException("Incoming recovery evidence is incomplete or conflicting.");

        await using var context = _database.Open(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var claim = await context.LockInvocationAsync(Guid.Parse(incoming.Identity.InvocationId), cancellationToken);
        if (claim.TargetId is not null)
        {
            if (claim.TargetId == instance.Id && await context.Instances.AsNoTracking().AnyAsync(row => row.Id == instance.Id, cancellationToken))
                return false;
            throw new InvalidOperationException("Incoming invocation claim conflicts with recovered Saga state.");
        }
        if (claim.Decision is not null)
        {
            var decision = StorageJson.Read<IncomingInvocationDecision>(claim.Decision);
            if (decision.RejectionReason is not null || decision.Request.Identity != incoming.Identity ||
                decision.Request.Payload.GetRawText() != incoming.Payload.Value.GetRawText())
                throw new InvalidOperationException("Incoming admission conflicts with recovered evidence.");
        }
        var snapshot = StorageJson.Read<SagaInstance>(StorageJson.Write(instance));
        default(IncomingTerminalWrite).Validate(snapshot);
        var row = new SagaRow { Id = snapshot.Id };
        row.Write(snapshot);
        context.Instances.Add(row);
        claim.TargetId = row.Id;
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException error) when (StorageDatabase.IsInstanceCollision(error)) { return false; }
        await StorageDatabase.CommitAsync(transaction, cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async ValueTask SaveAsync(SagaInstance instance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(instance.Id);
        await using var context = _database.Open(cancellationToken);
        var snapshot = StorageJson.Read<SagaInstance>(StorageJson.Write(instance));
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        if (snapshot.Revision == 0)
        {
            EnsureIncomingUnchanged(null, snapshot.IncomingInvocation);
            var created = new SagaRow { Id = snapshot.Id };
            created.Write(snapshot);
            context.Instances.Add(created);
        }
        else
        {
            var existing = await context.LockInstanceAsync(snapshot.Id, cancellationToken)
                ?? throw new InvalidOperationException($"Saga '{snapshot.Id}' does not exist; create it with revision zero.");
            if (snapshot.Revision != existing.Revision)
                throw StaleRevision(snapshot.Id);
            var previous = existing.Read();
            EnsureIncomingUnchanged(previous.IncomingInvocation, snapshot.IncomingInvocation);
            IncomingTerminalWrite.Capture(previous).Validate(snapshot);
            existing.Write(snapshot);
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException error) when (StorageDatabase.IsInstanceCollision(error))
        {
            throw StaleRevision(snapshot.Id, error);
        }
        await StorageDatabase.CommitAsync(transaction, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<SagaInstance?> FindAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instanceId);
        await using var context = _database.Open(cancellationToken);
        var row = await context.Instances.AsNoTracking().SingleOrDefaultAsync(x => x.Id == instanceId, cancellationToken);
        return row?.Read();
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<SagaInstance>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var context = _database.Open(cancellationToken);
        var snapshots = new List<SagaInstance>();
        await foreach (var row in context.Instances.AsNoTracking().AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            snapshots.Add(row.Read());
        }
        return snapshots.OrderByDescending(state => state.CreatedAt).ThenBy(state => state.Id, StringComparer.Ordinal).ToArray();
    }

    /// <inheritdoc />
    public async ValueTask<TResult> UpdateAsync<TResult>(string instanceId, Func<SagaInstance, TResult> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instanceId);
        ArgumentNullException.ThrowIfNull(update);
        await using var context = _database.Open(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var row = await context.LockInstanceAsync(instanceId, cancellationToken)
            ?? throw new KeyNotFoundException($"Saga instance '{instanceId}' was not found.");
        var working = row.Read();
        var incoming = working.IncomingInvocation;
        var terminal = IncomingTerminalWrite.Capture(working);
        cancellationToken.ThrowIfCancellationRequested();
        var result = StorageDatabase.InvokeCallback(() => update(working));
        cancellationToken.ThrowIfCancellationRequested();
        EnsureIncomingUnchanged(incoming, working.IncomingInvocation);
        terminal.Validate(working);
        row.Write(working);
        await context.SaveChangesAsync(cancellationToken);
        await StorageDatabase.CommitAsync(transaction, cancellationToken);
        return result;
    }

    /// <inheritdoc />
    public ValueTask<SagaInstance> GetOrCreateIncomingAsync(ProcessInvocationRequest request, Func<SagaInstance> create,
        DateTimeOffset acceptedAt, CancellationToken cancellationToken = default) =>
        _incoming.ClaimAsync(request, create, null, acceptedAt, cancellationToken);

    /// <inheritdoc />
    public ValueTask<SagaInstance> AttachIncomingAsync(string targetInstanceId, ProcessInvocationRequest request,
        DateTimeOffset acceptedAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targetInstanceId);
        return _incoming.ClaimAsync(request, null, targetInstanceId, acceptedAt, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IncomingInvocationDecision?> FindIncomingDecisionAsync(string invocationId, CancellationToken cancellationToken = default) =>
        _incoming.FindDecisionAsync(invocationId, cancellationToken);

    /// <inheritdoc />
    public ValueTask<IncomingInvocationDecision> RecordIncomingDecisionAsync(IncomingInvocationDecision decision, CancellationToken cancellationToken = default) =>
        _incoming.RecordDecisionAsync(decision, cancellationToken);

    private static InvalidOperationException StaleRevision(string id, Exception? cause = null) =>
        new($"Stale Saga state revision for '{id}'; reload state or use UpdateAsync.", cause);

    /// <summary>Only a claim can add or change incoming provenance.</summary>
    private static void EnsureIncomingUnchanged(IncomingInvocation? before, IncomingInvocation? after)
    {
        if (ReferenceEquals(before, after)) return;
        if (before is null || after is null || before.Identity != after.Identity || before.CreatedAt != after.CreatedAt
            || before.AcceptedAt != after.AcceptedAt || before.Payload?.GetRawText() != after.Payload?.GetRawText()
            || before.RequestMessage != after.RequestMessage)
            throw new InvalidOperationException("Incoming provenance may only be changed by the atomic claim operation.");
    }
}
