namespace SagaFlow.Core.Abstractions;

public interface ISagaFlow
{
    Task<Guid> StartAsync<TPayload>(TPayload payload, CancellationToken cancellationToken = default);
}
