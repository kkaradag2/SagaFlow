using System.Text.RegularExpressions;

namespace SagaFlow.Engine.ProcessDefinition;

/// <summary>Syntax only; no property resolution, scripting, or execution is performed.</summary>
public sealed record BooleanCondition(string PropertyPath, bool IsEquality, bool ExpectedValue)
{
    /// <summary>Normalized Boolean match for static exclusivity/coverage checks, not runtime evaluation.</summary>
    public bool RequiredValue => IsEquality ? ExpectedValue : !ExpectedValue;
}

public static partial class ConditionParser
{
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
}
