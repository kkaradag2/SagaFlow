using SagaFlow.Core.Definitions;

namespace SagaFlow.Core.Abstractions;

public interface ISagaDefinitionLoader
{
    ValueTask<SagaDefinition> LoadAsync(string path, CancellationToken cancellationToken = default);
}
