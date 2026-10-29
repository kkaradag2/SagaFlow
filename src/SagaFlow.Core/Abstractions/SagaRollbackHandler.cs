namespace SagaFlow.Core.Abstractions;

/// <summary>Application supplied rollback for the local work that started a Saga.</summary>
public abstract class SagaRollbackHandler<TPayload>
{
    public abstract Task RollbackAsync(TPayload payload, CancellationToken cancellationToken);
}
