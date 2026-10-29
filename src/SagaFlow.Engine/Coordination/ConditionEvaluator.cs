using System.Globalization;
using System.Numerics;
using System.Text.Json;
using SagaFlow.Core.Definitions;
using SagaFlow.Core.State;

namespace SagaFlow.Engine.Coordination;

internal static class ConditionEvaluator
{
    public static bool Matches(SagaConditionDefinition condition, JsonElement outcomeResult, SagaInstance? instance)
    {
        var path = condition.Path.Split('.');
        JsonElement value;
        int offset;
        switch (path[0])
        {
            case "context" when instance is not null: value = instance.Context; offset = 1; break;
            case "result": value = outcomeResult; offset = 1; break;
            case "steps" when instance is not null:
                var result = instance.Steps.SingleOrDefault(s => s.StepId == path[1])?.Result;
                if (!result.HasValue) return false;
                value = result.Value; offset = 3; break;
            default: return false;
        }
        foreach (var segment in path.Skip(offset))
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value)) return false;

        var expected = condition.Value;
        var valueIsBoolean = value.ValueKind is JsonValueKind.True or JsonValueKind.False;
        var expectedIsBoolean = expected.ValueKind is JsonValueKind.True or JsonValueKind.False;
        // A missing value or a JSON type mismatch never matches, including notEquals.
        if (value.ValueKind != expected.ValueKind && !(valueIsBoolean && expectedIsBoolean)) return false;
        var comparison = value.ValueKind switch
        {
            JsonValueKind.Number => CompareNumbers(value, expected),
            JsonValueKind.String => string.CompareOrdinal(value.GetString(), expected.GetString()),
            JsonValueKind.True or JsonValueKind.False => value.GetBoolean().CompareTo(expected.GetBoolean()),
            JsonValueKind.Null => 0,
            _ => (int?)null
        };
        if (!comparison.HasValue) return false;
        return condition.Operator switch
        {
            "equals" => comparison == 0,
            "notEquals" => comparison != 0,
            "greaterThan" => value.ValueKind == JsonValueKind.Number && comparison > 0,
            "lessThan" => value.ValueKind == JsonValueKind.Number && comparison < 0,
            _ => throw new InvalidOperationException($"Unsupported condition operator '{condition.Operator}'.")
        };
    }

    // Compare JSON numbers exactly. Avoid decimal/double rounding, overflow, and numeric string coercion.
    private static int CompareNumbers(JsonElement left, JsonElement right)
    {
        static (int Sign, string Digits, BigInteger Magnitude) Normalize(JsonElement number)
        {
            var raw = number.GetRawText();
            var sign = raw[0] == '-' ? -1 : 1;
            if (sign < 0) raw = raw[1..];
            var e = raw.IndexOfAny(['e', 'E']);
            var exponent = e < 0 ? BigInteger.Zero : BigInteger.Parse(raw.AsSpan(e + 1), CultureInfo.InvariantCulture);
            var mantissa = e < 0 ? raw : raw[..e];
            var dot = mantissa.IndexOf('.');
            if (dot >= 0) exponent -= mantissa.Length - dot - 1;
            var digits = mantissa.Replace(".", "", StringComparison.Ordinal).TrimStart('0');
            return digits.Length == 0 ? (0, "", BigInteger.Zero)
                : (sign, digits.TrimEnd('0'), exponent + digits.Length);
        }
        var a = Normalize(left);
        var b = Normalize(right);
        if (a.Sign != b.Sign) return a.Sign.CompareTo(b.Sign);
        if (a.Sign == 0) return 0;
        var magnitude = a.Magnitude.CompareTo(b.Magnitude);
        if (magnitude != 0) return a.Sign * magnitude;
        for (var i = 0; i < Math.Max(a.Digits.Length, b.Digits.Length); i++)
        {
            var digit = (i < a.Digits.Length ? a.Digits[i] : '0').CompareTo(i < b.Digits.Length ? b.Digits[i] : '0');
            if (digit != 0) return a.Sign * digit;
        }
        return 0;
    }
}
