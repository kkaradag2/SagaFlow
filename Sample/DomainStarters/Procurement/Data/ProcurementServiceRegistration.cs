using Npgsql;
using Sample.DomainStarters.Procurement.Services;

namespace Sample.DomainStarters.Procurement.Data;

internal static class ProcurementServiceRegistration
{
    public static void AddProcurementServices(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Procurement")
            ?? throw new InvalidOperationException("ConnectionStrings:Procurement is required.");
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddScoped<PurchaseRepository>();
        services.AddScoped<IProcurementService, ProcurementService>();
    }
}
