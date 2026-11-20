using Sample.DomainStarters.Order.Domain;
using Npgsql;

namespace Sample.DomainStarters.Order.Data;

internal sealed class PostgreSqlOrderRepository(NpgsqlDataSource dataSource)
{
    public async Task AddAsync(OrderDetails order, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var batch = new NpgsqlBatch(connection, transaction);

        var insertOrder = new NpgsqlBatchCommand("""
            INSERT INTO ordering.orders (id, customer_id, total, status, created_at)
            VALUES ($1, $2, $3, $4, $5)
            """);
        insertOrder.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = order.Id });
        insertOrder.Parameters.Add(new NpgsqlParameter<string> { TypedValue = order.CustomerId });
        insertOrder.Parameters.Add(new NpgsqlParameter<decimal> { TypedValue = order.Total });
        insertOrder.Parameters.Add(new NpgsqlParameter<string> { TypedValue = order.Status.ToString() });
        insertOrder.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = order.CreatedAt });
        batch.BatchCommands.Add(insertOrder);

        for (var index = 0; index < order.Items.Count; index++)
        {
            var item = order.Items[index];
            var insertItem = new NpgsqlBatchCommand("""
                INSERT INTO ordering.order_items (order_id, line_number, product_id, quantity, unit_price)
                VALUES ($1, $2, $3, $4, $5)
                """);
            insertItem.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = order.Id });
            insertItem.Parameters.Add(new NpgsqlParameter<int> { TypedValue = index + 1 });
            insertItem.Parameters.Add(new NpgsqlParameter<string> { TypedValue = item.ProductId });
            insertItem.Parameters.Add(new NpgsqlParameter<int> { TypedValue = item.Quantity });
            insertItem.Parameters.Add(new NpgsqlParameter<decimal> { TypedValue = item.UnitPrice });
            batch.BatchCommands.Add(insertItem);
        }

        await batch.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> CancelAsync(Guid orderId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE ordering.orders
            SET status = 'Cancelled', cancelled_at = COALESCE(cancelled_at, CURRENT_TIMESTAMP)
            WHERE id = $1
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = orderId });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }
}
