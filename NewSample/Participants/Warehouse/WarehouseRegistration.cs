using Npgsql;
using Sample.Participants.Warehouse.Data;
using Sample.Participants.Warehouse.Messaging;
using Sample.Participants.Warehouse.Services;
using Sample.Participants.Messaging;

namespace Sample.Participants.Warehouse;

internal static class WarehouseRegistration
{
    public static void AddWarehouse(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Warehouse")
            ?? throw new InvalidOperationException("ConnectionStrings:Warehouse is required.");

        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton(ParticipantRabbitMqSettings.Read(configuration));
        services.AddSingleton<InspectionRepository>();
        services.AddSingleton<InspectionOutbox>();
        services.AddSingleton<InspectionOutcomePublisher>();
        services.AddSingleton<IInspectionService, InspectionService>();
        services.AddSingleton<IParticipantCommandHandler, InspectionCommandHandler>();
        services.AddHostedService<RabbitMqParticipantWorker>();
        services.AddHostedService<InspectionDispatchWorker>();
    }
}
