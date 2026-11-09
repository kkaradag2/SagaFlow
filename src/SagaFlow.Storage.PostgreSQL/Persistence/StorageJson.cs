using System.Text.Json;
using System.Text.Json.Serialization;

namespace SagaFlow.Storage.PostgreSQL.Persistence;

/// <summary>Encodes storage documents independently of the messaging wire format.</summary>
internal static class StorageJson
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new RawJsonElementConverter() } };

    internal static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    internal static T Read<T>(string value) where T : class =>
        JsonSerializer.Deserialize<T>(value, Options) ?? throw new JsonException("The SagaFlow storage document is null.");

    // Storing the raw JSON as a string preserves whitespace, escapes, numeric spelling, and
    // the distinction between an absent nullable JsonElement and an explicit JSON null.
    private sealed class RawJsonElementConverter : JsonConverter<JsonElement>
    {
        public override JsonElement Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.Parse(reader.GetString() ?? throw new JsonException("Expected raw JSON text."));
            return document.RootElement.Clone();
        }

        public override void Write(Utf8JsonWriter writer, JsonElement value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.GetRawText());
    }
}
