using System.Text.Json;

namespace Mediathek.Crawlers;

/// <summary>
/// Safe navigation helpers over System.Text.Json's JsonElement.
/// Mirrors what MServer's JsonUtils does, without exceptions on missing keys.
/// </summary>
internal static class JsonNav
{
    /// <summary>Walk a path of property names. Returns null if any step is missing.</summary>
    public static JsonElement? Path(this JsonElement el, params string[] keys)
    {
        JsonElement cur = el;
        foreach (var key in keys)
        {
            if (cur.ValueKind != JsonValueKind.Object) return null;
            if (!cur.TryGetProperty(key, out cur))    return null;
        }
        return cur;
    }

    public static JsonElement? Path(this JsonElement? el, params string[] keys)
        => el.HasValue ? el.Value.Path(keys) : null;

    public static string? Str(this JsonElement el, string key)
        => el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    public static string? Str(this JsonElement? el)
        => el?.ValueKind == JsonValueKind.String ? el.Value.GetString() : null;

    public static bool? Bool(this JsonElement el, string key)
        => el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True  ? true
         : el.TryGetProperty(key, out    v) && v.ValueKind == JsonValueKind.False ? false
         : null;

    public static int? Int(this JsonElement el, string key)
        => el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32() : null;

    public static long? Long(this JsonElement el, string key)
        => el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt64() : null;

    public static IEnumerable<JsonElement> Array(this JsonElement? el)
        => el?.ValueKind == JsonValueKind.Array
            ? el.Value.EnumerateArray()
            : [];

    public static IEnumerable<JsonElement> Array(this JsonElement el)
        => el.ValueKind == JsonValueKind.Array ? el.EnumerateArray() : [];

    public static IEnumerable<JsonElement> Array(this JsonElement el, string key)
        => el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray()
            : [];
}
