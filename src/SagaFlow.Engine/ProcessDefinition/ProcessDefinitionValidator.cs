using System.Text.RegularExpressions;
using SagaFlow.Core.Definitions;

namespace SagaFlow.Engine.ProcessDefinition;

public sealed partial class ProcessDefinitionValidator
{
    public void Validate(SagaDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var errors = new List<string>();
        var context = $"Process '{definition.Domain}/{definition.Name}@{definition.Version}'";
        void Check(bool valid, string message) { if (!valid) errors.Add($"{context}: {message}"); }
        Check(IsIdentifier(definition.Name), "name must be an identifier (letters/digits/underscore/hyphen, starting with a letter).");
        Check(IsIdentifier(definition.Domain), "domain must be an identifier.");
        Check(IsVersion(definition.Version), "version must be a numeric major.minor or major.minor.patch (e.g. '1.0').");
        Check(definition.Steps is { Count: > 0 }, "steps must contain at least one step.");
        Check(definition.Steps is null || definition.Steps.Count <= 256, "steps exceeds the PoC limit of 256.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in definition.Steps ?? [])
        {
            if (step is null) { Check(false, "steps cannot contain null entries."); continue; }
            Check(IsIdentifier(step.Name) && step.Name is not ("completed" or "compensation"), $"step '{step.Name}' has an invalid or reserved name.");
            Check(names.Add(step.Name ?? ""), $"duplicate step '{step.Name}'.");
        }
        foreach (var step in definition.Steps ?? [])
        {
            if (step is null) continue;
            var prefix = $"step '{step.Name}'";
            Check(IsIdentifier(step.Command), $"{prefix}: command must be a nonempty identifier.");
            Check(step.TimeoutLimit is null || TryParseDuration(step.TimeoutLimit, out _), $"{prefix}: timeoutLimit must be a positive duration such as '10s', '30s', or '2m'.");
            Check(step.MaxAttempts > 0, $"{prefix}: maxAttempts must be a positive integer.");
            if (step.Compensation is not null)
                Check(IsIdentifier(step.Compensation.Command), $"{prefix}: compensation.command must be a nonempty identifier.");
            Check(step.Transitions is { Count: > 0 }, $"{prefix}: transitions must not be empty.");
            Check(step.Transitions is null || step.Transitions.Count <= 128, $"{prefix}: transitions exceeds the PoC limit of 128.");
            var rules = new List<(SagaTransitionDefinition Transition, BooleanCondition? Condition)>();
            foreach (var transition in step.Transitions ?? [])
            {
                if (transition is null) { Check(false, $"{prefix}: transitions cannot contain null entries."); continue; }
                Check(transition.Event is "success" or "failure" or "timeout", $"{prefix}: transition event '{transition.Event}' must be success, failure, or timeout.");
                Check(transition.NextStep is "completed" or "compensation" || (transition.NextStep is not null && names.Contains(transition.NextStep)), $"{prefix}: unknown nextStep '{transition.NextStep}'.");
                Check(transition.Else is null or true, $"{prefix}, event '{transition.Event}': else must be true when supplied.");
                Check(transition.Else is null || transition.When is null, $"{prefix}, event '{transition.Event}': else must not also contain when.");
                BooleanCondition? condition = null;
                if (transition.When is not null)
                {
                    try
                    {
                        condition = ConditionParser.Parse(transition.When);
                        Check(condition.PropertyPath.StartsWith("result.", StringComparison.Ordinal),
                            $"{prefix}, event '{transition.Event}': invalid condition '{transition.When}'; property path must start with 'result.'.");
                    }
                    catch (FormatException ex) { Check(false, $"{prefix}, event '{transition.Event}': invalid condition '{transition.When}'; {ex.Message}"); }
                }
                rules.Add((transition, condition));
            }
            foreach (var group in rules.GroupBy(r => r.Transition.Event, StringComparer.Ordinal))
            {
                var eventContext = $"{prefix}, event '{group.Key}'";
                var defaults = group.Count(r => r.Transition.When is null);
                Check(defaults <= 1, $"{eventContext}: multiple fallbacks/defaults; allow at most one else or unconditional transition.");
                var conditionalRules = group.Where(r => r.Condition is not null).ToArray();
                var conditions = conditionalRules.Select(r => r.Condition!).ToArray();
                for (var i = 0; i < conditions.Length; i++)
                    for (var j = i + 1; j < conditions.Length; j++)
                        Check(conditions[i].PropertyPath == conditions[j].PropertyPath && conditions[i].RequiredValue != conditions[j].RequiredValue,
                            $"{eventContext}: overlapping or equivalent conditions '{conditionalRules[i].Transition.When}' and '{conditionalRules[j].Transition.When}'; mutual exclusivity must be provable on the same Boolean property.");
                var exhaustive = conditions.Length == 2 && conditions[0].PropertyPath == conditions[1].PropertyPath && conditions[0].RequiredValue != conditions[1].RequiredValue;
                Check(conditions.Length == 0 || defaults == 1 || exhaustive, $"{eventContext}: missing fallback; provide else: true, one unconditional default, or complementary conditions on the same Boolean property.");
            }
        }
        Check(definition.BigPicture is not null, "bigPicture must be a sequence, not null.");
        Check(definition.BigPicture is null || definition.BigPicture.Count <= 128, "bigPicture exceeds the PoC limit of 128 references.");
        var sources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in definition.BigPicture ?? [])
        {
            if (source is null) { Check(false, "bigPicture cannot contain null entries."); continue; }
            Check(IsIdentifier(source.Domain) && IsIdentifier(source.Process), "bigPicture domain and process must be identifiers.");
            Check(source.Version is null || IsVersion(source.Version), $"bigPicture '{source.Domain}/{source.Process}': invalid version.");
            Check(TryEndpoint(source.Endpoint, out _), $"bigPicture '{source.Domain}/{source.Process}': endpoint must be an absolute HTTP(S) origin with no credentials, path, query, or fragment.");
            Check(sources.Add($"{source.Domain}/{source.Process}@{source.Version}"), $"duplicate bigPicture reference '{source.Domain}/{source.Process}@{source.Version}'.");
        }
        if (errors.Count == 0) ValidateGraph(definition, Check);
        if (errors.Count > 0) throw new DefinitionValidationException(errors);
    }

    private static void ValidateGraph(SagaDefinition definition, Action<bool, string> check)
    {
        // Only structurally/conditionally valid definitions reach this phase.
        var steps = definition.Steps.ToDictionary(s => s.Name, StringComparer.Ordinal);
        var edges = steps.ToDictionary(pair => pair.Key,
            pair => ViableTransitions(pair.Value).Select(t => t.NextStep!).ToArray(), StringComparer.Ordinal);
        var startStep = definition.Steps[0].Name;
        var reachable = new HashSet<string>(StringComparer.Ordinal) { startStep };
        var pending = new Queue<string>();
        pending.Enqueue(startStep);
        while (pending.TryDequeue(out var name))
            foreach (var target in edges[name])
                if (steps.ContainsKey(target) && reachable.Add(target)) pending.Enqueue(target);

        foreach (var name in steps.Keys.Except(reachable, StringComparer.Ordinal))
            check(false, $"step '{name}' is unreachable from first step '{startStep}'.");

        // Gray nodes are on the active DFS path; black nodes may safely be shared by DAG branches.
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);
        var path = new List<string>();
        void Visit(string name)
        {
            visited.Add(name);
            active.Add(name);
            path.Add(name);
            foreach (var target in edges[name])
            {
                if (!steps.ContainsKey(target)) continue; // Reserved terminals are not executable nodes.
                if (active.Contains(target))
                    check(false, $"executable step cycle detected: {string.Join(" -> ", path.Skip(path.IndexOf(target)).Append(target))}; each step may execute only once per Saga instance.");
                else if (!visited.Contains(target)) Visit(target);
            }
            path.RemoveAt(path.Count - 1);
            active.Remove(name);
        }
        Visit(startStep);

        var predecessors = steps.Keys.ToDictionary(name => name, _ => new List<string>(), StringComparer.Ordinal);
        var terminalViable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, targets) in edges)
            foreach (var target in targets)
                if (target is "completed" or "compensation") terminalViable.Add(name);
                else predecessors[target].Add(name);
        foreach (var name in terminalViable) pending.Enqueue(name);
        while (pending.TryDequeue(out var name))
            foreach (var predecessor in predecessors[name])
                if (terminalViable.Add(predecessor)) pending.Enqueue(predecessor);

        check(terminalViable.Contains(startStep), $"first step '{startStep}' has no reachable terminal path to completed or compensation.");
        foreach (var name in reachable.Except(terminalViable, StringComparer.Ordinal))
            check(false, $"step '{name}' cannot reach a terminal outcome (closed cycle or dead end).");
    }

    private static IEnumerable<SagaTransitionDefinition> ViableTransitions(SagaStepDefinition step)
    {
        foreach (var group in step.Transitions.GroupBy(t => t.Event, StringComparer.Ordinal))
        {
            var conditional = group.Where(t => t.When is not null).ToArray();
            // Two validated, exclusive Boolean conditions are exhaustive, so a fallback cannot contribute an exit.
            IEnumerable<SagaTransitionDefinition> viable = conditional.Length == 2 ? conditional : group;
            foreach (var transition in viable)
                yield return transition;
        }
    }

    public static bool IsIdentifier(string? value) => value is { Length: <= 128 } && Identifier().IsMatch(value);
    public static bool IsVersion(string? value) => value is { Length: <= 64 } && VersionPattern().IsMatch(value);
    public static bool IsPositiveDuration(string? value) => TryParseDuration(value, out _);

    public static bool TryParseDuration(string? value, out TimeSpan duration)
    {
        duration = default;
        if (value is not { Length: <= 32 }) return false;
        var match = DurationPattern().Match(value);
        if (!match.Success || !long.TryParse(match.Groups[1].Value, out var amount) || amount <= 0) return false;
        try
        {
            duration = match.Groups[2].Value switch
            {
                "ms" => TimeSpan.FromMilliseconds(amount),
                "s" => TimeSpan.FromSeconds(amount),
                "m" => TimeSpan.FromMinutes(amount),
                "h" => TimeSpan.FromHours(amount),
                _ => throw new InvalidOperationException()
            };
            return true;
        }
        catch (OverflowException) { return false; }
    }

    public static bool TryEndpoint(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https"
            && parsed.UserInfo.Length == 0 && parsed.AbsolutePath == "/" && parsed.Query.Length == 0 && parsed.Fragment.Length == 0)
        { uri = parsed; return true; }
        uri = null!;
        return false;
    }

    [GeneratedRegex(@"\A[A-Za-z][A-Za-z0-9_-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();
    [GeneratedRegex(@"\A(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:\.(?:0|[1-9][0-9]*))?\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
    [GeneratedRegex(@"\A([1-9][0-9]*)(ms|s|m|h)\z", RegexOptions.CultureInvariant)]
    private static partial Regex DurationPattern();
}
