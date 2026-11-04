using Microsoft.Extensions.DependencyInjection;
using SagaFlow.Core.Messages;
using SagaFlow.Core.Abstractions;
using SagaFlow.Engine.Messaging;
using SagaFlow.Engine.Coordination;
namespace SagaFlow.Hosting.DependencyInjection;

internal sealed class LocalSagaExecutionBoundary(IServiceScopeFactory scopes) : ILocalSagaExecutionBoundary
{
    public async Task ExecuteAsync(SagaCommand command, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<InvocationExecutionContext>();
        context.Bind(command);
        try
        {
            foreach (var handler in scope.ServiceProvider.GetServices<ILocalSagaStepHandler>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                await handler.ExecuteAsync(command, cancellationToken);
            }
            if (scope.ServiceProvider.GetService<IProcessInvocationBus>() is not null)
                await scope.ServiceProvider.GetRequiredService<ProcessInvocationPublisher>()
                    .PublishForStepAsync(command.Headers.SagaInstanceId, command.Headers.StepId, cancellationToken);
        }
        finally { context.Clear(); }
    }
}
