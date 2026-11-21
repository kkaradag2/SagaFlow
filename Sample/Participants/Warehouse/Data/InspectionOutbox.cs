using Npgsql;
using NpgsqlTypes;
using Sample.Participants.Warehouse.Messaging;

namespace Sample.Participants.Warehouse.Data;

internal sealed class InspectionOutbox(
    NpgsqlDataSource dataSource, InspectionOutcomePublisher publisher, ILogger<InspectionOutbox> logger)
{
    public async Task<bool> TryPublishAsync(Guid? returnId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // The bounded publish runs under the row lock. A crash releases it and the next attempt reuses MessageId.
        await using var claim = new NpgsqlCommand("""
            SELECT o.return_id, o.message_id, r.accepted, r.reply_to, r.correlation_id, r.headers::text
            FROM inspection.outbox o JOIN inspection.requests r USING (return_id)
            WHERE o.published_at IS NULL AND o.next_attempt_at <= CURRENT_TIMESTAMP
                AND ($1 IS NULL OR o.return_id = $1)
            ORDER BY o.next_attempt_at, o.return_id
            LIMIT 1 FOR UPDATE OF o SKIP LOCKED
            """, connection, transaction);
        claim.Parameters.Add(new NpgsqlParameter<Guid?> { NpgsqlDbType = NpgsqlDbType.Uuid, TypedValue = returnId });

        InspectionPublication publication;
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
            logger.LogWarning("Inspection outcome {MessageId} for return {ReturnId} will be retried: {ErrorType}",
                publication.MessageId, publication.ReturnId, error.GetType().Name);
        }

        await using var update = new NpgsqlCommand("""
            UPDATE inspection.outbox SET attempts = attempts + 1,
                published_at = CASE WHEN $2 THEN clock_timestamp() ELSE NULL END,
                next_attempt_at = clock_timestamp() + interval '2 seconds'
            WHERE return_id = $1
            """, connection, transaction);
        update.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = publication.ReturnId });
        update.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = published });
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return published;
    }
}
