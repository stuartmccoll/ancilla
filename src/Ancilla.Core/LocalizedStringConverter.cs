using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ancilla.Core;

/// <summary>
/// The Halo CMS returns display text as a localised object, e.g.
/// <c>{"value": "Bronze", "translations": {"fr-FR": "Bronze", ...}}</c>, but sometimes as a
/// plain string. This reads either shape into a string, taking the default <c>value</c>.
/// </summary>
public sealed class LocalizedStringConverter : JsonConverter<string?>
{
    public override bool HandleNull => true;

    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                return reader.GetString();
            case JsonTokenType.StartObject:
                using (var doc = JsonDocument.ParseValue(ref reader))
                {
                    foreach (var prop in doc.RootElement.EnumerateObject())
                        if (prop.Name.Equals("value", StringComparison.OrdinalIgnoreCase) &&
                            prop.Value.ValueKind == JsonValueKind.String)
                            return prop.Value.GetString();
                    return null;
                }
            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
