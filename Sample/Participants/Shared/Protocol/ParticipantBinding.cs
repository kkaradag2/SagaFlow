using System.Text.Json;
using System.Text.Json.Serialization;

namespace SagaFlow.Participants.Messaging;

// Sample application binding only; no execution metadata or SagaFlow runtime dependency.
public static class ParticipantBinding
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true
    };

    public static T Bind<T>(this ParticipantCommand command) where T : class
    {
        if (command.Payload.ValueKind != JsonValueKind.Object)
            throw new JsonException($"Command '{command.Name}' requires a JSON object for {typeof(T).Name}.");
        try
        {
            return command.Payload.Deserialize<T>(Options)
                ?? throw new JsonException("Business request must not be null.");
        }
        catch (JsonException error)
        {
            throw new JsonException($"Cannot bind command '{command.Name}' to {typeof(T).Name}: {error.Message}", error);
        }
    }
}

// Failure injection is an optional sample control, not a required business input.
public sealed record SampleFailureRequest(
    [property: JsonConverter(typeof(SampleFailureTargetConverter))] string? FailureAt = null);

// Preserve the existing rule: non-string failureAt values do not request a failure.
public sealed class SampleFailureTargetConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return reader.GetString();
        reader.Skip();
        return null;
    }
    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}
