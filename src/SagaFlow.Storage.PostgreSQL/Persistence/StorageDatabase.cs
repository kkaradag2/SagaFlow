using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace SagaFlow.Storage.PostgreSQL.Persistence;

/// <summary>Creates a separate EF Core context per operation and enforces the synchronous callback boundary.</summary>
internal sealed class StorageDatabase
{
    private static readonly AsyncLocal<bool> InCallback = new();
    private readonly DbContextOptions<StorageContext> _options;

    internal StorageDatabase(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        NpgsqlConnectionStringBuilder settings;
        try
        {
            settings = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            throw new ArgumentException("Use a valid Npgsql connection string for SagaFlow PostgreSQL storage.", nameof(connectionString));
        }
        if (string.IsNullOrWhiteSpace(settings.Host) || string.IsNullOrWhiteSpace(settings.Database))
            throw new ArgumentException("Specify Host and Database for SagaFlow PostgreSQL storage.", nameof(connectionString));

        _options = new DbContextOptionsBuilder<StorageContext>().UseNpgsql(settings.ConnectionString).Options;
    }

    internal StorageContext Open(CancellationToken cancellationToken)
    {
        if (InCallback.Value)
            throw new InvalidOperationException("SagaFlow PostgreSQL callbacks must not call the storage provider.");
        cancellationToken.ThrowIfCancellationRequested();
        return new StorageContext(_options);
    }

    internal static T InvokeCallback<T>(Func<T> callback)
    {
        InCallback.Value = true;
        try { return callback(); }
        finally { InCallback.Value = false; }
    }

    internal static Task CommitAsync(IDbContextTransaction transaction, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Honor cancellation until commit begins. A canceled commit could hide a successful server commit.
        return transaction.CommitAsync(CancellationToken.None);
    }

    internal static bool IsInstanceCollision(DbUpdateException error) =>
        error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "pk_sagaflow_instances" };
}
