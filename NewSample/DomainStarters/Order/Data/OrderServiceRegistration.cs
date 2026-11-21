using Sample.DomainStarters.Order.Services;
using Npgsql;

namespace Sample.DomainStarters.Order.Data;

public static class OrderServiceRegistration
{
    public static IServiceCollection AddOrderServices(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Order")
            ?? throw new InvalidOperationException("ConnectionStrings:Order is required.");

        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton<PostgreSqlOrderRepository>();
        services.AddScoped<IOrderService, OrderService>();
        return services;
    }
}
