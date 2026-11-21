using Npgsql;
using NpgsqlTypes;
using Sample.Participants.ManagerApproval.Messaging;

namespace Sample.Participants.ManagerApproval.Data;

internal sealed class ApprovalOutbox(
    NpgsqlDataSource dataSource, ApprovalOutcomePublisher publisher, ILogger<ApprovalOutbox> logger)
{
    public async Task<bool> TryPublishAsync(Guid? approvalId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // The bounded publish runs under the row lock. A crash releases it and the next attempt reuses MessageId.
        await using var claim = new NpgsqlCommand("""
            SELECT o.approval_id, o.message_id, r.approved, r.reply_to, r.correlation_id, r.headers::text
            FROM approval.outbox o JOIN approval.requests r ON r.id = o.approval_id
            WHERE o.published_at IS NULL AND o.next_attempt_at <= CURRENT_TIMESTAMP
                AND ($1 IS NULL OR o.approval_id = $1)
            ORDER BY o.next_attempt_at, o.approval_id
            LIMIT 1 FOR UPDATE OF o SKIP LOCKED
            """, connection, transaction);
        claim.Parameters.Add(new NpgsqlParameter<Guid?> { NpgsqlDbType = NpgsqlDbType.Uuid, TypedValue = approvalId });

        ApprovalPublication publication;
        await using (var reader = await claim.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
                return false;

            publication = new(reader.GetGuid(0), reader.GetGuid(1), reader.GetBoolean(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5));
        }

        var published = false;
        try
        {
            await publisher.PublishAsync(publication, cancellationToken);
            published = true;
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Outcome {MessageId} for approval {ApprovalId} will be retried: {ErrorType}",
                publication.MessageId, publication.ApprovalId, error.GetType().Name);
        }

        await using var update = new NpgsqlCommand("""
            UPDATE approval.outbox SET attempts = attempts + 1,
                published_at = CASE WHEN $2 THEN clock_timestamp() ELSE NULL END,
                next_attempt_at = clock_timestamp() + interval '2 seconds'
            WHERE approval_id = $1
            """, connection, transaction);
        update.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = publication.ApprovalId });
        update.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = published });
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return published;
    }
}
