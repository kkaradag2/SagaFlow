using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SagaFlow.Core.Abstractions;
using SagaFlow.Engine.Messaging;

namespace SagaFlow.Hosting.Messaging;

internal sealed class OutboxDispatchService(IServiceScopeFactory scopes, ILogger<OutboxDispatchService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var provider = scope.ServiceProvider;
                if (provider.GetService<ISagaMessageBus>() is not null)
                    await provider.GetRequiredService<CommandDispatcher>().DispatchOutboxAsync(
                        provider.GetRequiredService<ISagaStateStore>(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogError(error, "SagaFlow outbox dispatch pass failed; pending messages will be retried."); }
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) break; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
