using Npgsql;
using NpgsqlTypes;

namespace Sample.DomainStarters.Return.Data;

internal sealed record ReturnCase(
    Guid ReturnId, Guid OrderId, string CustomerId, decimal Amount, IReadOnlyList<ReturnItem> Items, string? FailStep);

internal sealed record ReturnItem(string ProductId, int Quantity);

internal sealed record ReturnReference(
    Guid OrderId, string CustomerId, Guid? SagaInstanceId, bool SagaStartUnconfirmed);

internal sealed class PostgreSqlReturnRepository(NpgsqlDataSource dataSource)
{
    public async Task AddAsync(ReturnCase entry, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var header = new NpgsqlCommand("""
            INSERT INTO returns.return_cases (return_id, order_id, customer_id, amount, fail_step)
            VALUES ($1, $2, $3, $4, $5)
            """, connection, transaction);
        header.Parameters.Add(new() { Value = entry.ReturnId });
        header.Parameters.Add(new() { Value = entry.OrderId });
        header.Parameters.Add(new() { Value = entry.CustomerId });
        header.Parameters.Add(new() { Value = entry.Amount });
        header.Parameters.Add(new() { NpgsqlDbType = NpgsqlDbType.Varchar, Value = (object?)entry.FailStep ?? DBNull.Value });
        await header.ExecuteNonQueryAsync(cancellationToken);

        await using var lines = new NpgsqlBatch(connection, transaction);
        for (var index = 0; index < entry.Items.Count; index++)
        {
            var item = entry.Items[index];
            lines.BatchCommands.Add(new("""
                INSERT INTO returns.return_items (return_id, line_number, product_id, quantity)
                VALUES ($1, $2, $3, $4)
                """)
            {
                Parameters =
                {
                    new() { Value = entry.ReturnId },
                    new() { Value = index + 1 },
                    new() { Value = item.ProductId },
                    new() { Value = item.Quantity }
                }
            });
        }

        await lines.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RecordSagaStartAsync(Guid returnId, Guid? sagaId, bool unconfirmed)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE returns.return_cases SET saga_instance_id = $2, saga_start_unconfirmed = $3 WHERE return_id = $1
            """);
        command.Parameters.Add(new() { Value = returnId });
        command.Parameters.Add(new() { NpgsqlDbType = NpgsqlDbType.Uuid, Value = (object?)sagaId ?? DBNull.Value });
        command.Parameters.Add(new() { Value = unconfirmed });
        await command.ExecuteNonQueryAsync();
    }

    public async Task<ReturnReference?> FindAsync(Guid returnId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT order_id, customer_id, saga_instance_id, saga_start_unconfirmed
            FROM returns.return_cases WHERE return_id = $1
            """);
        command.Parameters.Add(new() { Value = returnId });
        await using var row = await command.ExecuteReaderAsync(cancellationToken);
        if (!await row.ReadAsync(cancellationToken))
            return null;

        return new(row.GetGuid(0), row.GetString(1), row.IsDBNull(2) ? null : row.GetGuid(2), row.GetBoolean(3));
    }
}
