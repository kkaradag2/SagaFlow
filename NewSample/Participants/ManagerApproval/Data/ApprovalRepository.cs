using Npgsql;
using NpgsqlTypes;
using Sample.Participants.ManagerApproval.Domain;

namespace Sample.Participants.ManagerApproval.Data;

internal sealed class ApprovalRepository(NpgsqlDataSource dataSource)
{
    private const string ApprovalQuery = """
        SELECT r.id, r.order_id, r.total_quantity, r.requested_at, r.approved, r.decided_at, o.published_at
        FROM approval.requests r LEFT JOIN approval.outbox o ON o.approval_id = r.id
        """;

    public async Task<bool> RegisterAsync(Guid orderId, int totalQuantity, string commandId, string replyTo,
        string correlationId, string headers, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO approval.requests AS existing (id, order_id, total_quantity, command_id, reply_to, correlation_id, headers)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            ON CONFLICT (command_id) DO UPDATE SET command_id = EXCLUDED.command_id
            WHERE existing.order_id = EXCLUDED.order_id AND existing.total_quantity = EXCLUDED.total_quantity
                AND existing.reply_to = EXCLUDED.reply_to AND existing.correlation_id = EXCLUDED.correlation_id
                AND existing.headers = EXCLUDED.headers
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = Guid.NewGuid() });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = orderId });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = totalQuantity });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = commandId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = replyTo });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = correlationId });
        command.Parameters.Add(new NpgsqlParameter<string> { NpgsqlDbType = NpgsqlDbType.Jsonb, TypedValue = headers });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<ApprovalRecord>> GetPendingAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            ApprovalQuery + " WHERE r.approved IS NULL ORDER BY r.requested_at, r.id");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var approvals = new List<ApprovalRecord>();
        while (await reader.ReadAsync(cancellationToken))
            approvals.Add(ReadApproval(reader));
        return approvals;
    }

    public async Task<ApprovalRecord?> FindAsync(Guid approvalId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(ApprovalQuery + " WHERE r.id = $1");
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = approvalId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadApproval(reader) : null;
    }

    public async Task<ApprovalRecord?> DecideAsync(Guid approvalId, bool approved, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var setDecision = new NpgsqlCommand("""
            UPDATE approval.requests SET approved = $2, decided_at = CURRENT_TIMESTAMP
            WHERE id = $1 AND approved IS NULL
            """, connection, transaction);
        setDecision.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = approvalId });
        setDecision.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = approved });
        if (await setDecision.ExecuteNonQueryAsync(cancellationToken) == 1)
        {
            await using var enqueue = new NpgsqlCommand("""
                INSERT INTO approval.outbox (approval_id, message_id) VALUES ($1, $2)
                """, connection, transaction);
            enqueue.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = approvalId });
            enqueue.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = Guid.NewGuid() });
            await enqueue.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return await FindAsync(approvalId, cancellationToken);
    }

    private static ApprovalRecord ReadApproval(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetGuid(1), reader.GetInt32(2), reader.GetFieldValue<DateTimeOffset>(3),
        reader.IsDBNull(4) ? null : reader.GetBoolean(4),
        reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
        reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6));
}
