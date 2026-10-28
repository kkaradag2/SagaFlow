using System.Text.Json;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Definitions;
using SagaFlow.Core.State;

namespace SagaFlow.Engine.Coordination;

public sealed class SagaInstanceManager(ISagaClock clock)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public SagaInstance Create<TPayload>(SagaDefinition definition, TPayload payload)
    {
        JsonElement context;
        try { context = JsonSerializer.SerializeToElement(payload, JsonOptions); }
        catch (Exception error) when (error is JsonException or NotSupportedException)
        { throw new ArgumentException("Saga payload must be JSON-compatible and must not contain reference cycles.", nameof(payload), error); }
        var now = clock.UtcNow;
        return new()
        {
            DefinitionName = definition.Name, DefinitionDomain = definition.Domain, DefinitionVersion = definition.Version,
            CorrelationId = Guid.NewGuid().ToString("N"), Status = SagaStatus.Running,
            CurrentStep = definition.Steps[0].Name, Context = context, CreatedAt = now, UpdatedAt = now,
            Steps = Array.AsReadOnly(definition.Steps.Select(step => new SagaStepState { StepName = step.Name, Command = step.Command }).ToArray())
        };
    }
}
