using System.Text.Json;
using SagaFlow.Core.Definitions;
using SagaFlow.Engine.ProcessDefinition;

namespace SagaFlow.Engine.Coordination;

public sealed class TransitionResolver
{
    public string Resolve(SagaStepDefinition step, bool success, JsonElement result)
    {
        var category = success ? "success" : "failure";
        var candidates = step.Transitions.Where(rule => rule.Event == category).ToArray();
        SagaTransitionDefinition? match = null;
        foreach (var rule in candidates.Where(rule => rule.When is not null))
        {
            var condition = ConditionParser.Parse(rule.When!);
            var path = condition.PropertyPath.Split('.');
            if (path[0] != "result") throw new InvalidOperationException($"Step '{step.Name}': runtime conditions support result.* paths only, not '{condition.PropertyPath}'.");
            var value = result;
            foreach (var segment in path.Skip(1))
            {
                if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
                    throw new InvalidOperationException($"Step '{step.Name}': missing result property '{condition.PropertyPath}'.");
            }
            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidOperationException($"Step '{step.Name}': '{condition.PropertyPath}' must be a Boolean.");
            if (value.GetBoolean() == condition.RequiredValue)
            {
                if (match is not null) throw new InvalidOperationException($"Step '{step.Name}': multiple '{category}' transitions match.");
                match = rule;
            }
        }
        match ??= candidates.SingleOrDefault(rule => rule.When is null);
        return match?.NextStep ?? throw new InvalidOperationException($"Step '{step.Name}': no matching '{category}' transition or fallback.");
    }
}
