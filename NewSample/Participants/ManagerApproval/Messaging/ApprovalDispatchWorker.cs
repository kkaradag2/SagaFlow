using Npgsql;
using Sample.Participants.ManagerApproval.Data;

namespace Sample.Participants.ManagerApproval.Messaging;

internal sealed class ApprovalDispatchWorker(ApprovalOutbox outbox, ILogger<ApprovalDispatchWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await outbox.TryPublishAsync(null, stoppingToken))
                    continue;
            }
            catch (NpgsqlException error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(error, "Could not process the approval outbox; retrying");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}
