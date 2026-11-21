using Npgsql;
using Sample.DomainStarters.Return.Services;

namespace Sample.DomainStarters.Return.Data;

internal static class ReturnServiceRegistration
{
    public static void AddReturnServices(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Return")
            ?? throw new InvalidOperationException("ConnectionStrings:Return is required.");

        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddScoped<PostgreSqlReturnRepository>();
        services.AddScoped<IReturnService, ReturnService>();
    }
}
