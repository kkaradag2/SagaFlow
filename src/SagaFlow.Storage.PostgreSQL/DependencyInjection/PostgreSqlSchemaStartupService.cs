using Microsoft.Extensions.Hosting;
using Npgsql;
using SagaFlow.Storage.PostgreSQL.Schema;

namespace SagaFlow.Storage.PostgreSQL.DependencyInjection;

/// <summary>Completes version-aware schema initialization before any hosted service starts.</summary>
internal sealed class PostgreSqlSchemaStartupService(string connectionString) : IHostedLifecycleService
{
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await PostgreSqlStorageSchema.InitializeAsync(connectionString, cancellationToken);
        }
        catch (Exception error) when (error is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            throw new InvalidOperationException(
                "Cannot initialize SagaFlow PostgreSQL storage. Check PostgreSQL readiness, credentials, and schema version.", error);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
