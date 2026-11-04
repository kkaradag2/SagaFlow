using Microsoft.Extensions.DependencyInjection;
using SagaFlow.Core.Messages;
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
        }
        finally { context.Clear(); }
    }
}
