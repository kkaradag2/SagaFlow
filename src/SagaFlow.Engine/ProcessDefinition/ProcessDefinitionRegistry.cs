using SagaFlow.Core.Definitions;

namespace SagaFlow.Engine.ProcessDefinition;

public sealed record ProcessDefinitionId(string Domain, string Name, string Version)
{
    public static ProcessDefinitionId From(SagaDefinition definition) => new(definition.Domain, definition.Name, definition.Version);
}

public enum DefinitionOrigin { Local, Remote }
public sealed record RegisteredDefinition(SagaDefinition Definition, DefinitionOrigin Origin, string? Endpoint);

public sealed class ProcessDefinitionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<ProcessDefinitionId, RegisteredDefinition> _definitions = [];

    public RegisteredDefinition? Find(ProcessDefinitionId id)
    {
        lock (_gate) return _definitions.GetValueOrDefault(id);
    }

    public IReadOnlyList<RegisteredDefinition> Snapshot()
    {
        lock (_gate) return Array.AsReadOnly(_definitions.Values.OrderBy(x => x.Definition.Domain, StringComparer.Ordinal)
            .ThenBy(x => x.Definition.Name, StringComparer.Ordinal).ThenBy(x => x.Definition.Version, StringComparer.Ordinal).ToArray());
    }

    public void RegisterLocalBatch(IEnumerable<SagaDefinition> definitions)
    {
        var batch = definitions.Select(d => { new ProcessDefinitionValidator().Validate(d); return DefinitionSnapshot.Copy(d); }).ToArray();
        if (batch.Select(ProcessDefinitionId.From).Distinct().Count() != batch.Length)
            throw new DefinitionValidationException(["Duplicate process identity/version in local batch."]);
        lock (_gate)
        {
            if (batch.Any(d => _definitions.TryGetValue(ProcessDefinitionId.From(d), out var existing) && existing.Origin == DefinitionOrigin.Local))
                throw new DefinitionValidationException(["A local process identity/version is already registered."]);
            foreach (var definition in batch)
                _definitions[ProcessDefinitionId.From(definition)] = new(definition, DefinitionOrigin.Local, null);
        }
    }

    public bool RegisterRemote(SagaDefinition definition, string endpoint)
    {
        new ProcessDefinitionValidator().Validate(definition);
        if (!ProcessDefinitionValidator.TryEndpoint(endpoint, out _)) throw new ArgumentException("Invalid remote endpoint.", nameof(endpoint));
        lock (_gate)
        {
            // First snapshot wins; remote discovery cannot replace local ownership or an existing snapshot.
            return _definitions.TryAdd(ProcessDefinitionId.From(definition), new(DefinitionSnapshot.Copy(definition), DefinitionOrigin.Remote, endpoint));
        }
    }
}
