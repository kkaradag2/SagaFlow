using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Definitions;

namespace SagaFlow.Engine.ProcessDefinition;

public sealed class YamlSagaDefinitionLoader : ISagaDefinitionLoader
{
    public ValueTask<SagaDefinition> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var definition = new SagaDefinition { Name = Path.GetFileNameWithoutExtension(path) };
        return ValueTask.FromResult(definition);
    }
}
