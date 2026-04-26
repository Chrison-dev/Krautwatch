using MediathekNext.Crawlers.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediathekNext.Infrastructure.Crawling;

public sealed class RawResponseStoreOptions
{
    public const string SectionName = "RawResponseStore";

    /// <summary>
    /// Directory where raw JSON responses are stored.
    /// Defaults to a 'raw-responses' subfolder inside the current directory.
    /// </summary>
    public string BaseDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MediathekNext", "raw-responses");
}

/// <summary>
/// File-based raw response store. Writes each fetched JSON to:
///   {BaseDirectory}/{source}/{itemId}.json
///
/// Useful for debugging and replaying crawls without re-fetching.
/// </summary>
public sealed class RawResponseStore(
    IOptions<RawResponseStoreOptions> options,
    ILogger<RawResponseStore> log) : IRawResponseStore
{
    private readonly string _base = options.Value.BaseDirectory;

    public async Task StoreAsync(string source, string itemId, string json, CancellationToken ct = default)
    {
        try
        {
            var dir = Path.Combine(_base, source);
            Directory.CreateDirectory(dir);

            var safeId = SanitizeFileName(itemId);
            var path   = Path.Combine(dir, safeId + ".json");

            await File.WriteAllTextAsync(path, json, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Non-critical — log and continue rather than failing the crawl
            log.LogWarning(ex, "Failed to store raw response for {Source}/{ItemId}", source, itemId);
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }
}
