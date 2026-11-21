using Npgsql;
using Sample.Participants.Warehouse.Data;

namespace Sample.Participants.Warehouse.Messaging;

internal sealed class InspectionDispatchWorker(InspectionOutbox outbox, ILogger<InspectionDispatchWorker> logger)
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
                logger.LogWarning(error, "Could not process the inspection outbox; retrying");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}
