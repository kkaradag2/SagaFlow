using Npgsql;
using Sample.Participants.Inventory.Domain;

namespace Sample.Participants.Inventory.Data;

internal enum ReservationResult
{
    Reserved,
    InsufficientStock,
    Conflict
}

internal sealed class InventoryRepository(NpgsqlDataSource dataSource)
{
    public async Task<ReservationResult> ReserveAsync(
        Guid orderId, IReadOnlyList<InventoryLine> lines, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using var create = new NpgsqlCommand("""
            INSERT INTO inventory.reservations (order_id) VALUES ($1)
            ON CONFLICT (order_id) DO NOTHING
            """, connection, transaction);
        create.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = orderId });
        if (await create.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            var existing = await ReadLinesAsync(connection, transaction, orderId, cancellationToken);
            return existing.SequenceEqual(lines) ? ReservationResult.Reserved : ReservationResult.Conflict;
        }

        foreach (var line in lines)
        {
            await using var reserveStock = new NpgsqlCommand("""
                UPDATE inventory.stock
                SET available_quantity = available_quantity - $2
                WHERE product_id = $1 AND available_quantity >= $2
                """, connection, transaction);
            reserveStock.Parameters.Add(new NpgsqlParameter<string> { TypedValue = line.ProductId });
            reserveStock.Parameters.Add(new NpgsqlParameter<int> { TypedValue = line.Quantity });
            if (await reserveStock.ExecuteNonQueryAsync(cancellationToken) == 0)
                return ReservationResult.InsufficientStock;

            await using var saveLine = new NpgsqlCommand("""
                INSERT INTO inventory.reservation_items (order_id, product_id, quantity)
                VALUES ($1, $2, $3)
                """, connection, transaction);
            saveLine.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = orderId });
            saveLine.Parameters.Add(new NpgsqlParameter<string> { TypedValue = line.ProductId });
            saveLine.Parameters.Add(new NpgsqlParameter<int> { TypedValue = line.Quantity });
            await saveLine.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return ReservationResult.Reserved;
    }

    public async Task<bool> ReleaseAsync(Guid orderId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using var markReleased = new NpgsqlCommand("""
            UPDATE inventory.reservations
            SET status = 'Released', released_at = CURRENT_TIMESTAMP
            WHERE order_id = $1 AND status = 'Reserved'
            """, connection, transaction);
        markReleased.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = orderId });
        if (await markReleased.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            await using var wasReleased = new NpgsqlCommand("""
                SELECT EXISTS (
                    SELECT 1 FROM inventory.reservations WHERE order_id = $1 AND status = 'Released'
                )
                """, connection, transaction);
            wasReleased.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = orderId });
            return await wasReleased.ExecuteScalarAsync(cancellationToken) is true;
        }

        var lines = await ReadLinesAsync(connection, transaction, orderId, cancellationToken);
        foreach (var line in lines)
        {
            await using var restore = new NpgsqlCommand("""
                UPDATE inventory.stock
                SET available_quantity = available_quantity + $2
                WHERE product_id = $1
                """, connection, transaction);
            restore.Parameters.Add(new NpgsqlParameter<string> { TypedValue = line.ProductId });
            restore.Parameters.Add(new NpgsqlParameter<int> { TypedValue = line.Quantity });
            await restore.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task<InventoryLine[]> ReadLinesAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid orderId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT product_id, quantity FROM inventory.reservation_items WHERE order_id = $1
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = orderId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var lines = new List<InventoryLine>();
        while (await reader.ReadAsync(cancellationToken))
            lines.Add(new(reader.GetString(0), reader.GetInt32(1)));

        return lines.OrderBy(line => line.ProductId, StringComparer.Ordinal).ToArray();
    }
}
