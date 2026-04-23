using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SharpCompress.Compressors.Xz;

namespace MediathekNext.Infrastructure.Catalog.MediathekView;

/// <summary>
/// Parses the MediathekView filmliste format.
///
/// The filmliste is a non-standard JSON structure compressed with XZ/LZMA2.
/// Key quirks that must be handled:
///   1. All entries use the key "X" — not a standard JSON array of objects
///   2. Empty string fields mean "same value as the previous entry" (delta encoding)
///      This applies to Channel [0] and Topic [1] most commonly
///   3. HD/Small URLs may be full URLs or just suffixes appended to the SD URL base
///   4. There is no stable unique ID — we generate one via SHA-256 hash
/// </summary>
public class FilmlisteParser(ILogger<FilmlisteParser> logger)
{
    // Channels we care about for MVP — filter everything else during parse
    private static readonly HashSet<string> SupportedChannels = new(StringComparer.OrdinalIgnoreCase)
    {
        "ARD", "ZDF"
    };

    public async IAsyncEnumerable<FilmlisteEntry> ParseAsync(
        Stream xzStream,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Decompress XZ on the fly — never materialise the full ~500MB JSON in memory
        await using var decompressed = new XZStream(xzStream);

        using var jsonDoc = await JsonDocument.ParseAsync(decompressed, cancellationToken: ct);
        var root = jsonDoc.RootElement;

        // Track previous values for delta-decoding
        string prevChannel = "";
        string prevTopic = "";

        int total = 0;
        int skipped = 0;

        foreach (var property in root.EnumerateObject())
        {
            // Skip the metadata header "Filmliste" key
            if (property.Name != "X")
                continue;

            ct.ThrowIfCancellationRequested();

            if (property.Value.ValueKind != JsonValueKind.Array)
                continue;

            var arr = property.Value;
            if (arr.GetArrayLength() < 19)
            {
                logger.LogWarning("Skipping malformed entry with {FieldCount} fields", arr.GetArrayLength());
                continue;
            }

            total++;

            // Read all 19 fields
            var fields = new string[19];
            int i = 0;
            foreach (var element in arr.EnumerateArray())
            {
                if (i >= 19) break;
                fields[i++] = element.GetString() ?? "";
            }

            // Apply delta encoding — empty = inherit from previous entry
            var channel = string.IsNullOrEmpty(fields[0]) ? prevChannel : fields[0];
            var topic   = string.IsNullOrEmpty(fields[1]) ? prevTopic   : fields[1];

            prevChannel = channel;
            prevTopic   = topic;

            // Filter to supported channels only
            if (!SupportedChannels.Contains(channel))
            {
                skipped++;
                continue;
            }

            yield return new FilmlisteEntry(
                Channel:     channel,
                Topic:       topic,
                Title:       fields[2],
                Date:        fields[3],
                Time:        fields[4],
                Duration:    fields[5],
                SizeMb:      fields[6],
                Description: fields[7],
                UrlSd:       fields[8],
                Website:     fields[9],
                UrlSubtitle: fields[10],
                UrlRtmp:     fields[11],
                UrlHd:       fields[12],
                UrlHdRtmp:   fields[13],
                UrlSmall:    fields[14],
                UrlSmallRtmp:fields[15],
                UrlHistory:  fields[16],
                Geo:         fields[17],
                IsNew:       fields[18]
            );
        }

        logger.LogInformation(
            "Filmliste parsed: {Total} total entries, {Skipped} skipped (unsupported channels), {Kept} kept",
            total, skipped, total - skipped);
    }
}
