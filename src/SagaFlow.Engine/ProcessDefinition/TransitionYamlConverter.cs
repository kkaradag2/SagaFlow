using System.Text.Json;
using SagaFlow.Core.Definitions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace SagaFlow.Engine.ProcessDefinition;

/// <summary>Accept both legacy event/when scalars and on/when mappings without changing the legacy C# API.</summary>
internal sealed class TransitionYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(SagaTransitionDefinition);

    public object ReadYaml(IParser parser, Type type, ObjectDeserializer deserialize)
    {
        parser.Consume<MappingStart>();
        string? category = null, when = null, nextStep = null;
        bool? otherwise = null;
        SagaConditionDefinition? condition = null;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        while (!parser.TryConsume<MappingEnd>(out _))
        {
            var key = parser.Consume<Scalar>();
            if (!keys.Add(key.Value is "on" ? "event" : key.Value))
                throw new YamlException(key.Start, key.End, $"Duplicate transition key '{key.Value}' (on and event are aliases).");
            switch (key.Value)
            {
                case "on": case "event": category = (string?)deserialize(typeof(string)); break;
                case "nextStep": nextStep = (string?)deserialize(typeof(string)); break;
                case "else": otherwise = (bool?)deserialize(typeof(bool?)); break;
                case "when":
                    if (parser.Accept<MappingStart>(out _)) condition = (SagaConditionDefinition?)deserialize(typeof(SagaConditionDefinition));
                    else when = (string?)deserialize(typeof(string));
                    break;
                default: throw new YamlException(key.Start, key.End, $"Unknown transition property '{key.Value}'.");
            }
        }
        return new SagaTransitionDefinition { Event = category ?? string.Empty, When = when, Condition = condition, Else = otherwise, NextStep = nextStep };
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serialize)
    {
        var transition = (SagaTransitionDefinition)value!;
        emitter.Emit(new MappingStart());
        void Write(string key, object item) { emitter.Emit(new Scalar(key)); serialize(item); }
        Write("event", transition.Event);
        if (transition.Condition is not null) Write("when", transition.Condition);
        else if (transition.When is not null) Write("when", transition.When);
        if (transition.Else.HasValue) Write("else", transition.Else.Value);
        if (transition.NextStep is not null) Write("nextStep", transition.NextStep);
        emitter.Emit(new MappingEnd());
    }
}

/// <summary>Preserve JSON scalar types, including quoted strings such as "true" or "70".</summary>
internal sealed class ConditionValueYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(JsonElement);

    public object ReadYaml(IParser parser, Type type, ObjectDeserializer deserialize)
    {
        var scalar = parser.Consume<Scalar>();
        if (!scalar.Tag.IsEmpty)
            throw new YamlException(scalar.Start, scalar.End, "Condition values use JSON scalar syntax; explicit YAML tags are unsupported.");
        var text = scalar.Value;
        if (scalar.Style is ScalarStyle.Plain or ScalarStyle.Any)
        {
            if (text is "null" or "~" or "") return JsonSerializer.SerializeToElement<object?>(null);
            try
            {
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number)
                    return document.RootElement.Clone();
            }
            catch (JsonException) { /* An unquoted YAML string is still a string. */ }
        }
        return JsonSerializer.SerializeToElement(text);
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serialize)
    {
        var json = (JsonElement)value!;
        if (json.ValueKind == JsonValueKind.String)
            emitter.Emit(new Scalar(null, null, json.GetString()!, ScalarStyle.DoubleQuoted, true, false));
        else emitter.Emit(new Scalar(json.GetRawText()));
    }
}
