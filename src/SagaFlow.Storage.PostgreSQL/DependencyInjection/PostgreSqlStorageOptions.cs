namespace SagaFlow.Storage.PostgreSQL.DependencyInjection;

/// <summary>Configures the database used to persist Saga coordination state.</summary>
public sealed class PostgreSqlStorageOptions
{
    public string? ConnectionString { get; set; }
}
