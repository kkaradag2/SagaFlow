using Npgsql;
using SagaFlow.Storage.InMemory.DependencyInjection;
using SagaFlow.Storage.PostgreSQL.DependencyInjection;
using SagaFlow.Storage.PostgreSQL.Schema;

namespace SagaFlow.Samples;

/// <summary>Applies the same explicit storage configuration to each sample Domain Starter.</summary>
internal static class SampleSagaStorage
{
    /// <summary>Selects the provider and initializes PostgreSQL before the host can start Saga execution.</summary>
    internal static async Task ConfigureAsync(WebApplicationBuilder builder, string domain)
    {
        const string providerKey = "SagaFlow:Storage:Provider";
        const string connectionKey = "SagaFlow:Storage:ConnectionString";
        var provider = builder.Configuration[providerKey];
        if (string.Equals(provider, "InMemory", StringComparison.OrdinalIgnoreCase))
        {
            builder.Services.AddSagaFlowInMemoryStorage();
            return;
        }
        if (!string.Equals(provider, "PostgreSQL", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Set {providerKey} for {domain} to InMemory or PostgreSQL.");

        var connectionString = builder.Configuration[connectionKey];
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException($"Set {connectionKey} for {domain} when using PostgreSQL.");

        var database = $"sagaflow_{domain}";
        NpgsqlConnectionStringBuilder settings;
        try { settings = new NpgsqlConnectionStringBuilder(connectionString); }
        catch (ArgumentException)
        {
            throw new InvalidOperationException($"Set {connectionKey} to a valid PostgreSQL connection string for {domain}.");
        }
        if (string.IsNullOrWhiteSpace(settings.Host) || settings.Database != database || settings.Username != database)
            throw new InvalidOperationException($"Set {connectionKey} for {domain} with Host and both Database and Username set to '{database}'.");

        try
        {
            await PostgreSqlStorageSchema.InitializeAsync(settings.ConnectionString);
        }
        catch (Exception error) when (error is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            throw new InvalidOperationException(
                $"Cannot initialize SagaFlow PostgreSQL storage for {domain} in '{database}'. Check PostgreSQL readiness, credentials, and schema version.", error);
        }
        builder.Services.AddSagaFlowPostgreSqlStorage(settings.ConnectionString);
    }
}
