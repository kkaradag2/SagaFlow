using System.Text.Json;

namespace SagaFlow.Core.Definitions;

/// <summary>A typed JSON comparison, without scripting or expression evaluation.</summary>
public sealed class SagaConditionDefinition
{
    public string Path { get; init; } = string.Empty;
    public string Operator { get; init; } = string.Empty;
    public JsonElement Value { get; init; }
}
