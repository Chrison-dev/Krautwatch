using System.Globalization;
using Krautwatch.Domain.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Tvdb.Clients;
using Tvdb.Models;

namespace Krautwatch.Infrastructure.Tvdb;

/// <summary>
/// <see cref="ITvdbCatalog"/> over the first-party <c>TvdbClient</c> package.
/// </summary>
/// <remarks>
/// <para>
/// Every method swallows transport failures and returns nothing. TVDB is an <i>enrichment</i> source: if it
/// is down or unconfigured we fall back to title matching and emit releases without ids, which is worse but
/// still works. Throwing would surface as an indexer error, and Sonarr disables an indexer that keeps
/// failing — so a TVDB outage must not cost us the whole indexer.
/// </para>
/// <para>
/// Results are cached because a single Sonarr episode search fans out into several queries against the same
/// series, and an interactive search over a season multiplies that again.
/// </para>
/// </remarks>
public class TvdbCatalog(
    ISeriesClient series,
    ISearchClient search,
    TvdbApiKeySource keys,
    IMemoryCache cache,
    ILogger<TvdbCatalog> logger) : ITvdbCatalog
{
    /// <summary>
    /// Series records and episode lists barely change, and a stale season number is far cheaper than
    /// hammering a third-party API on every Sonarr poll.
    /// </summary>
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(12);

    /// <summary>TVDB pages episodes at 500; stop well before runaway paging on a huge series.</summary>
    private const int MaxEpisodePages = 20;

    /// <summary>ISO-3166-1 alpha-3 for Germany — the filter that makes title search usable.</summary>
    private const string GermanCountry = "deu";

    public bool IsConfigured => keys.IsConfigured;

    public bool IsKeyFromConfiguration => keys.Origin == TvdbKeyOrigin.Configuration;

    public async Task<TvdbSeries?> GetSeriesAsync(int tvdbId, CancellationToken ct = default)
    {
        if (!IsConfigured)
            return null;

        if (cache.TryGetValue($"tvdb:series:{tvdbId}", out TvdbSeries? cached))
            return cached;

        try
        {
            var record = await series.ExtendedAsync(tvdbId, cancellationToken: ct);
            if (record is null)
                return null;

            var mapped = Map(record);
            cache.Set($"tvdb:series:{tvdbId}", mapped, CacheDuration);
            return mapped;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TVDB series lookup failed for {TvdbId}", tvdbId);
            return null;
        }
    }

    public async Task<IReadOnlyList<TvdbEpisode>> GetEpisodesAsync(int tvdbId, CancellationToken ct = default)
    {
        if (!IsConfigured)
            return [];

        if (cache.TryGetValue($"tvdb:episodes:{tvdbId}", out IReadOnlyList<TvdbEpisode>? cached))
            return cached ?? [];

        try
        {
            var collected = new List<TvdbEpisode>();
            for (var page = 0; page < MaxEpisodePages; page++)
            {
                // "default" is the series' own season-type ordering — the same one Sonarr shows, so the
                // numbers we derive line up with the numbers it asks for.
                var response = await series.EpisodesGetAsync(page, tvdbId, "default", cancellationToken: ct);
                var episodes = response?.Episodes;
                if (episodes is null || episodes.Count == 0)
                    break;

                collected.AddRange(episodes.Select(Map).OfType<TvdbEpisode>());

                // The envelope carrying paging links is unwrapped before we see it, so a short page is the
                // end-of-list signal available to us.
                if (episodes.Count < 500)
                    break;
            }

            cache.Set($"tvdb:episodes:{tvdbId}", (IReadOnlyList<TvdbEpisode>)collected, CacheDuration);
            return collected;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TVDB episode lookup failed for {TvdbId}", tvdbId);
            return [];
        }
    }

    /// <remarks>
    /// One query, exactly as asked. The article-drop retry is a matching <i>policy</i> and lives in the
    /// Application layer with the rest of the ranking, not in the transport adapter.
    /// </remarks>
    public async Task<IReadOnlyList<TvdbSeries>> SearchAsync(string title, CancellationToken ct = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(title))
            return [];

        return await SearchOnceAsync(title, ct);
    }

    private async Task<IReadOnlyList<TvdbSeries>> SearchOnceAsync(string title, CancellationToken ct)
    {
        var key = $"tvdb:search:{title.ToLowerInvariant()}";
        if (cache.TryGetValue(key, out IReadOnlyList<TvdbSeries>? cached))
            return cached ?? [];

        try
        {
            var results = await search.SearchAsync(
                query: title, type: "series", country: GermanCountry, cancellationToken: ct);

            var mapped = (results ?? [])
                .Select(Map)
                .OfType<TvdbSeries>()
                .ToList();

            cache.Set(key, (IReadOnlyList<TvdbSeries>)mapped, CacheDuration);
            return mapped;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TVDB search failed for {Title}", title);
            return [];
        }
    }

    // ── mapping ───────────────────────────────────────────────

    private static TvdbSeries Map(SeriesExtendedRecord record)
    {
        var aliases = (record.Aliases ?? [])
            .Select(alias => alias.Name)
            .Concat(TranslatedNames(record))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new TvdbSeries(
            TvdbId: record.Id ?? 0,
            Name: record.Name ?? string.Empty,
            Year: ParseYear(record.Year),
            // Prefer the original network: the current rights-holder can differ from the broadcaster whose
            // Mediathek actually carries the show.
            Network: record.OriginalNetwork?.Name ?? record.LatestNetwork?.Name,
            Aliases: aliases);
    }

    private static IEnumerable<string?> TranslatedNames(SeriesExtendedRecord record) =>
        record.Translations?.NameTranslations?.Select(translation => translation.Name) ?? [];

    private static TvdbSeries? Map(SearchResult result)
    {
        if (!int.TryParse(result.Id?.Replace("series-", string.Empty), out var id))
            return null;

        return new TvdbSeries(
            TvdbId: id,
            Name: result.Name ?? string.Empty,
            Year: ParseYear(result.Year),
            Network: result.Network,
            Aliases: (result.Aliases ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).ToList());
    }

    private static TvdbEpisode? Map(EpisodeBaseRecord record)
    {
        if (record.SeasonNumber is not { } season || record.Number is not { } number)
            return null;

        return new TvdbEpisode(season, number, ParseDate(record.Aired), record.Name);
    }

    private static int? ParseYear(string? year) =>
        int.TryParse(year, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static DateOnly? ParseDate(string? aired) =>
        DateOnly.TryParse(aired, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
}
