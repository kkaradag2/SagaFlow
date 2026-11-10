using System.Data;
using Microsoft.EntityFrameworkCore;
using SagaFlow.Storage.PostgreSQL.Persistence;

namespace SagaFlow.Storage.PostgreSQL.Schema;

/// <summary>Explicitly provisions and verifies the supported SagaFlow PostgreSQL schema.</summary>
public static class PostgreSqlStorageSchema
{
    private const int CurrentVersion = 2;

    /// <summary>Applies the initial schema transactionally, or verifies the previously initialized version.</summary>
    public static async Task InitializeAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var context = new StorageDatabase(connectionString).Open(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        // All SagaFlow schema initializers in this database share this transaction-scoped lock.
        await context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(1731180915, 1)", cancellationToken);
        await context.Database.ExecuteSqlRawAsync("""
            CREATE SCHEMA IF NOT EXISTS sagaflow;
            CREATE TABLE IF NOT EXISTS sagaflow.schema_version (
                singleton boolean PRIMARY KEY CHECK (singleton),
                version integer NOT NULL CHECK (version > 0)
            );
            """, cancellationToken);

        var versions = await context.Database.SqlQueryRaw<int>(
            "SELECT version AS \"Value\" FROM sagaflow.schema_version").ToListAsync(cancellationToken);
        if (versions.Count == 0)
        {
            using var script = typeof(PostgreSqlStorageSchema).Assembly.GetManifestResourceStream("SagaFlow.PostgreSQL.V001.sql")
                ?? throw new InvalidOperationException("The SagaFlow PostgreSQL schema script is missing.");
            using var reader = new StreamReader(script);
            await context.Database.ExecuteSqlRawAsync(await reader.ReadToEndAsync(cancellationToken), cancellationToken);
            await ApplyV002Async(context, cancellationToken);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO sagaflow.schema_version (singleton, version) VALUES (true, {CurrentVersion})", cancellationToken);
        }
        else if (versions.Single() == 1)
        {
            await ApplyV002Async(context, cancellationToken);
            await context.Database.ExecuteSqlRawAsync("UPDATE sagaflow.schema_version SET version = 2", cancellationToken);
        }
        else if (versions.Single() != CurrentVersion)
        {
            throw new InvalidOperationException($"Unsupported SagaFlow PostgreSQL schema version {versions[0]}. Expected {CurrentVersion}.");
        }

        await StorageDatabase.CommitAsync(transaction, cancellationToken);
    }

    private static async Task ApplyV002Async(StorageContext context, CancellationToken cancellationToken)
    {
        using var script = typeof(PostgreSqlStorageSchema).Assembly.GetManifestResourceStream("SagaFlow.PostgreSQL.V002.sql")
            ?? throw new InvalidOperationException("The SagaFlow PostgreSQL outbox schema script is missing.");
        using var reader = new StreamReader(script);
        await context.Database.ExecuteSqlRawAsync(await reader.ReadToEndAsync(cancellationToken), cancellationToken);
    }
}
