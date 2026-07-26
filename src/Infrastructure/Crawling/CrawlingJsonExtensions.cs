using System.Text.Json;

namespace Krautwatch.Infrastructure.Crawling;

internal static class CrawlingJsonExtensions
{
    public static IEnumerable<JsonElement> GetPropertyOrEmptyArray(this JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray() : [];
}
