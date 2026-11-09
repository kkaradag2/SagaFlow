using System.Data;
using Microsoft.EntityFrameworkCore;
using SagaFlow.Core.Messages;
using SagaFlow.Core.State;
using SagaFlow.Storage.PostgreSQL.Models;
using SagaFlow.Storage.PostgreSQL.Persistence;

namespace SagaFlow.Storage.PostgreSQL;

/// <summary>Persists first-wins decisions and atomic target claims across database sessions.</summary>
internal sealed class IncomingInvocationStore(StorageDatabase database)
{
    internal async ValueTask<IncomingInvocationDecision?> FindDecisionAsync(string invocationId, CancellationToken cancellationToken)
    {
        await using var context = database.Open(cancellationToken);
        var id = Guid.Parse(invocationId);
        var decision = await context.Invocations.Where(row => row.Id == id).Select(row => row.Decision)
            .SingleOrDefaultAsync(cancellationToken);
        return decision is null ? null : StorageJson.Read<IncomingInvocationDecision>(decision);
    }

    /// <summary>Stores the first decision and replays it for the same identity and raw payload.</summary>
    internal async ValueTask<IncomingInvocationDecision> RecordDecisionAsync(IncomingInvocationDecision decision, CancellationToken cancellationToken)
    {
        decision.Reply.ValidateAgainst(decision.Request.Identity);
        await using var context = database.Open(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var row = await context.LockInvocationAsync(Guid.Parse(decision.Request.Identity.InvocationId), cancellationToken);
        if (row.Decision is not null)
        {
            var existing = StorageJson.Read<IncomingInvocationDecision>(row.Decision);
            ValidateSameRequest(existing.Request, decision.Request);
            cancellationToken.ThrowIfCancellationRequested();
            return existing;
        }
        if (row.TargetId is not null)
        {
            // Ordinary saga writes preserve provenance, so a committed read is enough for this check.
            var target = await context.Instances.AsNoTracking().SingleAsync(x => x.Id == row.TargetId, cancellationToken);
            ValidateClaimedRequest(target.Read(), decision.Request);
            if (decision.RejectionReason is not null)
                throw new InvalidOperationException("Admission conflicts with an existing target.");
        }

        row.Decision = StorageJson.Write(decision);
        await context.SaveChangesAsync(cancellationToken);
        await StorageDatabase.CommitAsync(transaction, cancellationToken);
        return decision;
    }

    /// <summary>Locks the invocation first and then the target. Both writes commit in one transaction.</summary>
    internal async ValueTask<SagaInstance> ClaimAsync(ProcessInvocationRequest request, Func<SagaInstance>? create,
        string? targetId, DateTimeOffset acceptedAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (acceptedAt == default) throw new ArgumentException("Acceptance time required.", nameof(acceptedAt));
        await using var context = database.Open(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var row = await context.LockInvocationAsync(Guid.Parse(request.Identity.InvocationId), cancellationToken);
        if (row.Decision is not null)
        {
            var decision = StorageJson.Read<IncomingInvocationDecision>(row.Decision);
            ValidateSameRequest(decision.Request, request);
            if (decision.RejectionReason is not null) throw new InvalidOperationException("Invocation was rejected.");
        }
        if (row.TargetId is not null)
        {
            if (targetId is not null && targetId != row.TargetId)
                throw new InvalidOperationException("Invocation already belongs to another target Saga.");
            var existing = await context.LockInstanceAsync(row.TargetId, cancellationToken)
                ?? throw new InvalidOperationException("The invocation claim references a missing target Saga.");
            var snapshot = existing.Read();
            ValidateClaimedRequest(snapshot, request);
            cancellationToken.ThrowIfCancellationRequested();
            return snapshot;
        }

        SagaInstance working;
        SagaRow target;
        if (targetId is null)
        {
            ArgumentNullException.ThrowIfNull(create);
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = StorageDatabase.InvokeCallback(create);
            working = StorageJson.Read<SagaInstance>(StorageJson.Write(candidate));
            target = new SagaRow { Id = working.Id };
        }
        else
        {
            target = await context.LockInstanceAsync(targetId, cancellationToken)
                ?? throw new KeyNotFoundException("Target Saga not found.");
            working = target.Read();
        }

        if (string.IsNullOrWhiteSpace(working.Id) || working.Status != SagaStatus.Running || working.IncomingInvocation is not null
            || new ProcessIdentity(working.DefinitionDomain, working.DefinitionName, working.DefinitionVersion) != request.Identity.Target)
            throw new InvalidOperationException("Invalid target state/process identity for incoming invocation.");
        if (targetId is null)
        {
            if (working.Revision != 0) throw new InvalidOperationException("Factory must return a new unsaved instance.");
            context.Instances.Add(target);
        }
        working.IncomingInvocation = new(request.Identity, request.Message.OccurredAt, acceptedAt.ToUniversalTime(), request.Payload.Clone());
        working.UpdatedAt = acceptedAt.ToUniversalTime();
        target.Write(working);
        row.TargetId = target.Id;

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException error) when (StorageDatabase.IsInstanceCollision(error))
        {
            throw new InvalidOperationException("Target Saga ID collision.", error);
        }
        await StorageDatabase.CommitAsync(transaction, cancellationToken);
        return working;
    }

    private static void ValidateSameRequest(ProcessInvocationRequest expected, ProcessInvocationRequest request)
    {
        if (expected.Identity != request.Identity || expected.Payload.GetRawText() != request.Payload.GetRawText())
            throw new InvalidOperationException("Invocation identity/payload conflict.");
    }

    private static void ValidateClaimedRequest(SagaInstance target, ProcessInvocationRequest request)
    {
        var incoming = target.IncomingInvocation;
        if (incoming?.Identity != request.Identity || incoming.Payload?.GetRawText() != request.Payload.GetRawText())
            throw new InvalidOperationException("Invocation identity/payload conflict.");
    }
}
