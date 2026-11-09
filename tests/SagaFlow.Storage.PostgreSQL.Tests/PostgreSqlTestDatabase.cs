using Npgsql;

namespace SagaFlow.Storage.PostgreSQL.Tests;

public sealed class PostgreSqlFactAttribute : FactAttribute
{
    public PostgreSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PostgreSqlTestDatabase.EnvironmentVariable)))
            Skip = $"Set {PostgreSqlTestDatabase.EnvironmentVariable} to run PostgreSQL integration tests.";
    }
}

/// <summary>Creates a fresh database per test. The configured administrative database is never modified directly.</summary>
internal sealed class PostgreSqlTestDatabase : IAsyncDisposable
{
    internal const string EnvironmentVariable = "SAGAFLOW_POSTGRESQL_TEST_CONNECTION";
    private readonly string _name = $"sagaflow_pbi02_{Guid.NewGuid():N}";
    private readonly string _adminConnection;
    private bool _created;

    internal PostgreSqlTestDatabase()
    {
        _adminConnection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(EnvironmentVariable))
        {
            Pooling = false
        }.ConnectionString;
        ConnectionString = new NpgsqlConnectionStringBuilder(_adminConnection) { Database = _name }.ConnectionString;
    }

    internal string ConnectionString { get; }

    internal async Task CreateAsync()
    {
        await ExecuteAsync(_adminConnection, $"CREATE DATABASE \"{_name}\"");
        _created = true;
    }

    internal static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_created) await ExecuteAsync(_adminConnection, $"DROP DATABASE \"{_name}\" WITH (FORCE)");
    }
}
