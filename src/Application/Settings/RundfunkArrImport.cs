using System.Text.Json;
using System.Text.Json.Serialization;
using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Interfaces;
using Krautwatch.Domain.ValueObjects;

namespace Krautwatch.Application.Settings;

/// <summary>
/// Reads RundfunkArr's <c>rulesets.json</c> into curated mapping hints.
/// </summary>
/// <remarks>
/// <para>
/// Format per their published schema (<c>.github/schemas/rulesets.schema.json</c>): an array whose entries
/// carry a <c>topic</c> — the Mediathek show name — and a nested <c>media</c> object holding
/// <c>media_tvdbId</c>. Measured against the live file on 2026-07-28: 110 entries, 109 with a TVDB id, one
/// movie. Their <c>shows.json</c> is deliberately not read — it is a single stub entry, not a catalog.
/// </para>
/// <para>
/// Their per-show <c>matchingStrategy</c> is ignored. They hand-assign one of five strategies; we derive the
/// equivalent automatically by trying episode titles then air dates, and the two shows they mark
/// <c>ItemTitleEqualsAirdate</c> are ones our automatic path already matches completely. Importing a
/// strategy we do not implement would be storing a field nothing reads.
/// </para>
/// </remarks>
public static class RundfunkArrRulesets
{
    public const string SourceName = "rundfunkarr";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Parses the file into hints, skipping entries with no TVDB id or no topic.
    /// </summary>
    /// <exception cref="JsonException">The payload is not the expected array shape.</exception>
    public static IReadOnlyList<ImportedShowHint> Parse(string json)
    {
        var entries = JsonSerializer.Deserialize<List<RulesetEntry>>(json, Options) ?? [];

        return entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Topic) && entry.Media?.TvdbId is > 0)
            .Select(entry => new ImportedShowHint
            {
                TvdbId = entry.Media!.TvdbId!.Value,
                Topic = entry.Topic!.Trim(),
                NormalizedTopic = TitleNormalizer.Normalize(entry.Topic),
                Source = SourceName,
            })
            .Where(hint => hint.NormalizedTopic.Length > 0)
            // One topic can appear under several rulesets (different priorities and regexes for the same
            // show). We only take the identity pair, so collapse them.
            .GroupBy(hint => (hint.TvdbId, hint.NormalizedTopic))
            .Select(group => group.First())
            .ToList();
    }

    private sealed record RulesetEntry
    {
        [JsonPropertyName("topic")] public string? Topic { get; init; }
        [JsonPropertyName("media")] public MediaRef? Media { get; init; }
    }

    private sealed record MediaRef
    {
        [JsonPropertyName("media_tvdbId")] public int? TvdbId { get; init; }
        [JsonPropertyName("media_name")] public string? Name { get; init; }
    }
}

// ══════════════════════════════════════════════════════════════
// Handler
// ══════════════════════════════════════════════════════════════

public record HintImportResult(int Stored, int Matched, IReadOnlyList<string> MatchedShows);

/// <summary>
/// Imports a curated hint set and reports how much of it this catalog can already use.
/// </summary>
/// <remarks>
/// The "matched" count is the honest part of the answer. Importing 109 curated pairs sounds like a lot;
/// how many correspond to shows this instance has actually crawled is usually far fewer, and an operator
/// should see that rather than assume their library is now covered.
/// </remarks>
public class ImportShowHintsHandler(
    IImportedShowHintRepository hints,
    IEpisodeRepository episodes)
{
    public async Task<HintImportResult> HandleAsync(
        string source,
        IReadOnlyList<ImportedShowHint> incoming,
        CancellationToken ct = default)
    {
        var stored = await hints.ReplaceSourceAsync(source, incoming, ct);

        var ourTitles = (await episodes.GetShowsAsync(ct: ct))
            .Select(row => row.Show)
            .ToLookup(show => TitleNormalizer.Normalize(show.Title), StringComparer.Ordinal);

        var matched = incoming
            .Where(hint => ourTitles.Contains(hint.NormalizedTopic))
            .Select(hint => hint.Topic)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(topic => topic, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new HintImportResult(stored, matched.Count, matched);
    }
}
