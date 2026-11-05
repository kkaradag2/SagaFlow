using System.Text.Json;
using SagaFlow.Core.Messages;
using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Definitions;
using SagaFlow.Core.State;
using SagaFlow.Engine.ProcessDefinition;

namespace SagaFlow.Engine.Coordination;

/// <summary>Builds an invocation from the engine's active instance/step, never from application-supplied source metadata.</summary>
public sealed class ProcessInvocationResolver(ProcessDefinitionRegistry definitions, ISagaClock? clock = null)
{
    public ProcessInvocationRequest Create<TPayload>(SagaInstance instance, string target, TPayload payload)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (instance.Status != SagaStatus.Running || string.IsNullOrWhiteSpace(instance.CurrentStep)
            || instance.Steps.Count(s => s.StepName == instance.CurrentStep && s.Status == SagaStepStatus.Dispatched) != 1)
            throw new InvalidOperationException("An active dispatched local Saga step is required for invocation.");
        var sourceId = new ProcessDefinitionId(instance.DefinitionDomain, instance.DefinitionName, instance.DefinitionVersion);
        var source = definitions.Find(sourceId);
        if (source?.Origin != DefinitionOrigin.Local || string.IsNullOrWhiteSpace(instance.CorrelationId))
            throw new InvalidOperationException("The active Saga's owning local definition and correlation are required.");
        if (!source.Definition.Steps.Any(s => s.Name == instance.CurrentStep))
            throw new InvalidOperationException("Active step is absent from the owning definition.");
        var targetId = ResolveTarget(source.Definition, target);
        JsonElement json;
        try { json = JsonSerializer.SerializeToElement(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (Exception error) when (error is JsonException or NotSupportedException)
        { throw new ArgumentException("Invocation payload must be JSON-compatible.", nameof(payload), error); }
        var step = instance.Steps.Single(s => s.StepName == instance.CurrentStep);
        if (step.Invocation is { } existing)
        {
            if (existing.Identity.Target != targetId || existing.Payload.GetRawText() != json.GetRawText())
                throw new InvalidOperationException("One invocation per executable step; repeated target and payload must match.");
            return existing;
        }
        var identity = new ProcessInvocationIdentity(Guid.NewGuid().ToString("N"), instance.CorrelationId,
            new(sourceId.Domain, sourceId.Name, sourceId.Version), instance.Id, instance.CurrentStep!, targetId, step.Execution?.ExecutionId);
        return step.Invocation = new(identity, json, new(identity, Guid.NewGuid().ToString("N"), clock?.UtcNow ?? DateTimeOffset.UtcNow, step.Execution?.CommandId));
    }

    public static ProcessIdentity ResolveTarget(SagaDefinition source, string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        var matches = source.BigPicture.Where(x => x.Relationship == "invokes" && x.Process == target).ToArray();
        if (matches.Length == 0) throw new KeyNotFoundException($"No declared invokes target named '{target}'.");
        if (matches.Length != 1) throw new InvalidOperationException($"Invocation target '{target}' is ambiguous.");
        var match = matches[0];
        if (string.IsNullOrWhiteSpace(match.Version))
            throw new InvalidOperationException($"Invocation target '{target}' requires an explicit version.");
        return new(match.Domain, match.Process, match.Version);
    }
}
