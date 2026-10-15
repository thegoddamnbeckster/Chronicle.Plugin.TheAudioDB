using System.Globalization;
using System.Text.Json;

namespace Chronicle.Plugin.TheAudioDB;

/// <summary>
/// One TheAudioDB record (an artist, an album or a track) as the service sent it. Read by field name, never by position or
/// count, because the service sends strings, numbers, nulls and empty strings for the same field and adds fields without notice;
/// whatever the plugin does not map stays available here and is kept in the item's extended data.
/// </summary>
internal sealed class Rec
{
    public JsonElement Json { get; }

    public Rec(JsonElement json) => Json = json;

    /// <summary>The field as text, or null when it is missing, null, empty or only white space.</summary>
    public string? S(string key)
    {
        if (Json.ValueKind != JsonValueKind.Object || !Json.TryGetProperty(key, out var v)) return null;
        var text = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>The field as a whole number, or null when it is not one.</summary>
    public int? I(string key) =>
        int.TryParse(S(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>The field as a decimal number (always read with a dot), or null when it is not one.</summary>
    public double? D(string key) =>
        double.TryParse(S(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;

    public string? Id(string key) => S(key);

    /// <summary>Every field the service sent, in its own order (for the extended data of an item).</summary>
    public IEnumerable<JsonProperty> Properties() =>
        Json.ValueKind == JsonValueKind.Object ? Json.EnumerateObject() : [];
}

/// <summary>Which kind of record an id or a result is.</summary>
public enum Kind { Artist, Album, Track }
