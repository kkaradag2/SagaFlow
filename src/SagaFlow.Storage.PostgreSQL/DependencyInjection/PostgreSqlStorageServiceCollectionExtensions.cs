using Microsoft.Extensions.DependencyInjection;
using SagaFlow.Core.Abstractions;

namespace SagaFlow.Storage.PostgreSQL.DependencyInjection;

/// <summary>Registers the PostgreSQL provider independently of the SagaFlow host.</summary>
public static class PostgreSqlStorageServiceCollectionExtensions
{
    /// <summary>Selects one singleton PostgreSQL store. Initialize the database schema separately.</summary>
    public static IServiceCollection AddSagaFlowPostgreSqlStorage(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddSingleton<ISagaStateStore>(new PostgreSqlSagaStateStore(connectionString));
    }
}
