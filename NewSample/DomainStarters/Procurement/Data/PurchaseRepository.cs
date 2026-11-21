using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Sample.DomainStarters.Procurement.Contracts;

namespace Sample.DomainStarters.Procurement.Data;

internal sealed record PurchaseReference(string EmployeeId, Guid? SagaInstanceId, bool StartUnconfirmed);

internal sealed class PurchaseRepository(NpgsqlDataSource dataSource)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task AddAsync(PurchasePayload purchase, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO purchasing.requests (id, employee_id, customer_id, items, fail_step)
            VALUES ($1, $2, $3, $4, $5)
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = purchase.Id });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = purchase.EmployeeId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = purchase.CustomerId });
        command.Parameters.Add(new NpgsqlParameter<string> { NpgsqlDbType = NpgsqlDbType.Jsonb, TypedValue = JsonSerializer.Serialize(purchase.Items, Json) });
        command.Parameters.Add(new() { NpgsqlDbType = NpgsqlDbType.Varchar, Value = (object?)purchase.FailStep ?? DBNull.Value });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RecordSagaStartAsync(Guid id, Guid? sagaId, bool unconfirmed)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE purchasing.requests SET saga_instance_id = $2, start_unconfirmed = $3 WHERE id = $1
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        command.Parameters.Add(new NpgsqlParameter<Guid?> { NpgsqlDbType = NpgsqlDbType.Uuid, TypedValue = sagaId });
        command.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = unconfirmed });
        await command.ExecuteNonQueryAsync();
    }

    public async Task<PurchaseReference?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT employee_id, saga_instance_id, start_unconfirmed FROM purchasing.requests WHERE id = $1
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.GetBoolean(2))
            : null;
    }
}
