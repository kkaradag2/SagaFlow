using System.Text.RegularExpressions;
using System.Text.Json;
using SagaFlow.Core.Definitions;

namespace SagaFlow.Engine.ProcessDefinition;

/// <summary>Syntax only; no property resolution, scripting, or execution is performed.</summary>
public sealed record BooleanCondition(string PropertyPath, bool IsEquality, bool ExpectedValue)
{
    /// <summary>Normalized Boolean match for static exclusivity/coverage checks, not runtime evaluation.</summary>
    public bool RequiredValue => IsEquality ? ExpectedValue : !ExpectedValue;
}

public static partial class ConditionParser
{
    public static void Validate(SagaConditionDefinition condition, IReadOnlySet<string> stepNames)
    {
        if (condition.Path is not { Length: > 0 and <= 512 } || !PathGrammar().IsMatch(condition.Path))
            throw new FormatException("Condition path must be a nonempty dot-separated property path (no indexing, calls, or expressions).");
        var parts = condition.Path.Split('.');
        var valid = parts[0] switch
        {
            "context" or "result" => parts.Length >= 2,
            "steps" => parts.Length >= 3 && parts[2] == "result" && stepNames.Contains(parts[1]),
            _ => false
        };
        if (!valid) throw new FormatException("Condition path must use context.*, result.*, or steps.<existingStep>.result[.*]. JSON property names are case-sensitive.");
        if (condition.Operator is not ("equals" or "notEquals" or "greaterThan" or "lessThan"))
            throw new FormatException($"Unsupported condition operator '{condition.Operator}'.");
        if (condition.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
            throw new FormatException("Condition value is required and must be a JSON scalar (string, number, Boolean, or null).");
        if (condition.Operator is "greaterThan" or "lessThan" && condition.Value.ValueKind != JsonValueKind.Number)
            throw new FormatException($"Operator '{condition.Operator}' requires a numeric value.");
    }

    public static BooleanCondition Parse(string expression)
    {
        if (expression.Length > 512)
            throw new FormatException("Condition exceeds 512 characters.");
        var match = Grammar().Match(expression);
        if (!match.Success)
            throw new FormatException("Supported condition: property.path == true|false or property.path != true|false. Calls, indexing, logical operators, and non-boolean literals are unsupported.");
        return new(match.Groups[1].Value, match.Groups[2].Value == "==", match.Groups[3].Value == "true");
    }

    [GeneratedRegex(@"\A\s*([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+)\s*(==|!=)\s*(true|false)\s*\z", RegexOptions.CultureInvariant)]
    private static partial Regex Grammar();

    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_-]*(?:\.[A-Za-z_][A-Za-z0-9_-]*)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex PathGrammar();
}
