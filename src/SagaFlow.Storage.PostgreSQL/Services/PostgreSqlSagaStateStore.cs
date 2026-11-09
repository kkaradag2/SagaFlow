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
            EnsureIncomingUnchanged(existing.Read().IncomingInvocation, snapshot.IncomingInvocation);
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
        cancellationToken.ThrowIfCancellationRequested();
        var result = StorageDatabase.InvokeCallback(() => update(working));
        cancellationToken.ThrowIfCancellationRequested();
        EnsureIncomingUnchanged(incoming, working.IncomingInvocation);
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
