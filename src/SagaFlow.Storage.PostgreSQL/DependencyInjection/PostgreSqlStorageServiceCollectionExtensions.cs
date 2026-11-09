using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SagaFlow.Core.Abstractions;
using SagaFlow.Hosting.DependencyInjection;

namespace SagaFlow.Storage.PostgreSQL.DependencyInjection;

/// <summary>Registers the PostgreSQL provider independently of the SagaFlow host.</summary>
public static class PostgreSqlStorageServiceCollectionExtensions
{
    /// <summary>Selects the PostgreSQL singleton store and verifies its schema before hosted services start.</summary>
    public static void UsePostgreSql(this SagaFlowStorageOptions storage, Action<PostgreSqlStorageOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        storage.Select(nameof(UsePostgreSql), services =>
        {
            var options = new PostgreSqlStorageOptions();
            configure(options);
            var connectionString = options.ConnectionString;
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException("Set the PostgreSQL ConnectionString when calling options.Storage.UsePostgreSql.");

            services.AddSagaFlowPostgreSqlStorage(connectionString);
            services.AddHostedService(_ => new PostgreSqlSchemaStartupService(connectionString));
        });
    }

    /// <summary>Selects one singleton PostgreSQL store. Initialize the database schema separately.</summary>
    public static IServiceCollection AddSagaFlowPostgreSqlStorage(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddSingleton<ISagaStateStore>(new PostgreSqlSagaStateStore(connectionString));
    }
}
