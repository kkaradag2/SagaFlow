using Npgsql;
using NpgsqlTypes;
using Sample.Participants.Warehouse.Domain;

namespace Sample.Participants.Warehouse.Data;

internal sealed class InspectionRepository(NpgsqlDataSource dataSource)
{
    private const string InspectionQuery = """
        SELECT r.inspection_id, r.return_id, r.order_id, r.requested_at, r.accepted, r.decided_at, o.published_at
        FROM inspection.requests r LEFT JOIN inspection.outbox o USING (return_id)
        """;

    public async Task<bool> RegisterAsync(Guid returnId, Guid orderId, string commandId, string replyTo,
        string correlationId, string headers, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO inspection.requests AS existing (inspection_id, return_id, order_id, command_id, reply_to, correlation_id, headers)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            ON CONFLICT (return_id) DO UPDATE SET command_id = EXCLUDED.command_id
            WHERE existing.command_id = EXCLUDED.command_id AND existing.order_id = EXCLUDED.order_id
                AND existing.reply_to = EXCLUDED.reply_to AND existing.correlation_id = EXCLUDED.correlation_id
                AND existing.headers = EXCLUDED.headers
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = Guid.NewGuid() });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = returnId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = orderId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = commandId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = replyTo });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = correlationId });
        command.Parameters.Add(new NpgsqlParameter<string> { NpgsqlDbType = NpgsqlDbType.Jsonb, TypedValue = headers });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<InspectionRecord>> GetPendingAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            InspectionQuery + " WHERE r.accepted IS NULL ORDER BY r.requested_at, r.return_id");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var inspections = new List<InspectionRecord>();
        while (await reader.ReadAsync(cancellationToken))
            inspections.Add(ReadInspection(reader));
        return inspections;
    }

    public async Task<InspectionRecord?> FindAsync(Guid returnId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(InspectionQuery + " WHERE r.return_id = $1");
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = returnId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadInspection(reader) : null;
    }

    public async Task<InspectionRecord?> DecideAsync(Guid returnId, bool accepted, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var setDecision = new NpgsqlCommand("""
            UPDATE inspection.requests SET accepted = $2, decided_at = CURRENT_TIMESTAMP
            WHERE return_id = $1 AND accepted IS NULL
            """, connection, transaction);
        setDecision.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = returnId });
        setDecision.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = accepted });
        if (await setDecision.ExecuteNonQueryAsync(cancellationToken) == 1)
        {
            await using var enqueue = new NpgsqlCommand("""
                INSERT INTO inspection.outbox (return_id, message_id) VALUES ($1, $2)
                """, connection, transaction);
            enqueue.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = returnId });
            enqueue.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = Guid.NewGuid() });
            await enqueue.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return await FindAsync(returnId, cancellationToken);
    }

    private static InspectionRecord ReadInspection(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetFieldValue<DateTimeOffset>(3),
        reader.IsDBNull(4) ? null : reader.GetBoolean(4),
        reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
        reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6));
}
