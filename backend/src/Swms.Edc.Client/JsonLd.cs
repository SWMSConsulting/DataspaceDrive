using System.Text.Json.Nodes;

namespace Swms.Edc.Client;

/// <summary>
/// Liest Antworten der Management-API unabhängig davon, ob der EDC Properties kompakt („assetId“), mit Präfix
/// („edc:assetId“) oder als volle IRI liefert. Werte in <c>{"@value": …}</c> und einelementige Listen werden entpackt.
/// </summary>
public static class JsonLd
{
    public static JsonNode? Get(JsonObject? obj, string localName)
    {
        if (obj is null)
            return null;
        foreach (var (key, value) in obj)
        {
            if (key == localName || key.EndsWith(':' + localName, StringComparison.Ordinal)
                || key.EndsWith('/' + localName, StringComparison.Ordinal) || key.EndsWith('#' + localName, StringComparison.Ordinal))
                return Unwrap(value);
        }
        return null;
    }

    public static string? GetString(JsonObject? obj, string localName) => AsString(Get(obj, localName));

    public static string? Id(JsonObject? obj) => obj?["@id"]?.GetValue<string>();

    /// <summary>Einzelwert oder Liste als Liste von Objekten (fehlend = leer).</summary>
    public static IReadOnlyList<JsonObject> Objects(JsonNode? node) => node switch
    {
        JsonObject single => [single],
        JsonArray array => array.OfType<JsonObject>().ToList(),
        _ => [],
    };

    public static string? AsString(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonValue value => value.ToJsonString(),
        JsonObject obj when obj["@id"] is JsonValue id => id.GetValue<string>(),
        _ => node.ToJsonString(),
    };

    /// <summary>Entpackt <c>{"@value": …}</c> und einelementige Listen.</summary>
    public static JsonNode? Unwrap(JsonNode? value) => value switch
    {
        JsonObject obj when obj.ContainsKey("@value") => obj["@value"],
        JsonArray { Count: 1 } array => Unwrap(array[0]),
        _ => value,
    };
}
