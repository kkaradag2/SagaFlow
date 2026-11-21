using Npgsql;

namespace Sample.Participants.Shipment.Data;

internal sealed class ShipmentRepository(NpgsqlDataSource dataSource)
{
    public async Task CreateAsync(Guid orderId, string trackingNumber, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO shipment.shipments (order_id, tracking_number)
            VALUES ($1, $2)
            ON CONFLICT (order_id) DO NOTHING
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = orderId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = trackingNumber });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> CancelAsync(Guid orderId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE shipment.shipments
            SET status = 'Cancelled', cancelled_at = COALESCE(cancelled_at, CURRENT_TIMESTAMP)
            WHERE order_id = $1
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = orderId });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }
}
