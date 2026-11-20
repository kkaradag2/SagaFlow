using Npgsql;

namespace Sample.Participants.Payment.Data;

internal sealed class PaymentRepository(NpgsqlDataSource dataSource)
{
    public async Task<bool> AuthorizeAsync(Guid orderId, decimal amount, CancellationToken cancellationToken)
    {
        // Repeated authorizations acknowledge the stored amount without changing a cancelled payment.
        await using var command = dataSource.CreateCommand("""
            INSERT INTO payment.authorizations AS current_payment (order_id, amount)
            VALUES ($1, $2)
            ON CONFLICT (order_id) DO UPDATE SET order_id = EXCLUDED.order_id
            WHERE current_payment.amount = EXCLUDED.amount
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = orderId });
        command.Parameters.Add(new NpgsqlParameter<decimal> { TypedValue = amount });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> RefundAsync(Guid orderId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE payment.authorizations
            SET status = 'Cancelled', cancelled_at = COALESCE(cancelled_at, CURRENT_TIMESTAMP)
            WHERE order_id = $1
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = orderId });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }
}
