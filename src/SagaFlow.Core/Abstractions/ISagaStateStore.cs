using SagaFlow.Core.State;

namespace SagaFlow.Core.Abstractions;

public interface ISagaStateStore
{
    ValueTask SaveAsync(SagaInstance instance, CancellationToken cancellationToken = default);

    ValueTask<SagaInstance?> FindAsync(string instanceId, CancellationToken cancellationToken = default);
}
