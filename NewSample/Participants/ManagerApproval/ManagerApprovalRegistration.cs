using Npgsql;
using Sample.Participants.ManagerApproval.Data;
using Sample.Participants.ManagerApproval.Messaging;
using Sample.Participants.ManagerApproval.Services;
using Sample.Participants.Messaging;

namespace Sample.Participants.ManagerApproval;

internal static class ManagerApprovalRegistration
{
    public static void AddManagerApproval(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("ManagerApproval")
            ?? throw new InvalidOperationException("ConnectionStrings:ManagerApproval is required.");

        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton(ParticipantRabbitMqSettings.Read(configuration));
        services.AddSingleton<ApprovalRepository>();
        services.AddSingleton<ApprovalOutbox>();
        services.AddSingleton<ApprovalOutcomePublisher>();
        services.AddSingleton<IApprovalService, ApprovalService>();
        services.AddSingleton<IParticipantCommandHandler, ApprovalCommandHandler>();
        services.AddHostedService<RabbitMqParticipantWorker>();
        services.AddHostedService<ApprovalDispatchWorker>();
    }
}
