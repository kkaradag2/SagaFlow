using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SagaFlow.Engine.Timeouts;

namespace SagaFlow.Hosting.Timeouts;

internal sealed class SagaTimeoutDetectionService(
    SagaTimeoutDetector detector,
    TimeProvider timeProvider,
    ILogger<SagaTimeoutDetectionService> logger) : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMilliseconds(250);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ScanInterval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await detector.DetectAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error) { logger.LogError(error, "Saga timeout detection scan failed."); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
