using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Interfaces;
using Krautwatch.Domain.ValueObjects;

namespace Krautwatch.Application.Indexing;

/// <summary>
/// A Newznab search (t=search / t=tvsearch) or, when <see cref="Q"/> is empty, the RSS feed of the
/// most recent releases. Optional season/episode narrow a Standard-series query.
/// </summary>
public record SearchReleasesQuery(
    string? Q = null,
    int? Season = null,
    int? Episode = null,
    int Limit = 100,
    int? TvdbId = null,
    /// <summary>
    /// Set when Sonarr asked for a <b>daily</b> episode — <c>season</c> is the year and <c>ep</c> is
    /// <c>MM/DD</c> (#95). Matching is then on air date, not on numbering, because a dated episode has
    /// no season or episode number at all.
    /// </summary>
    DateOnly? AirDate = null,
    /// <summary>
    /// Newznab's <c>offset</c> — how many results to skip. Used by an RSS client catching up after
    /// downtime, which pages back through history rather than re-reading the first page (#12).
    /// </summary>
    int Offset = 0,
    /// <summary>
    /// True when a whole season was asked for. For a dated show that means a whole <i>year</i>, so the
    /// season number is matched against either our numbering or the broadcast year.
    /// </summary>
    bool SeasonOnly = false);

/// <summary>
/// One page of Newznab results, with what the <c>newznab:response</c> element needs to describe it.
/// </summary>
/// <param name="Releases">The releases on this page.</param>
/// <param name="Offset">Where this page starts in the overall result set.</param>
/// <param name="Total">
/// How many results exist in total. Exact for the RSS feed, which is the path that gets paged; for a
/// search it is <c>Offset + Releases.Count</c> — the honest conservative answer, since we cap a search
/// at <c>limit</c> without counting the rest, and reporting more would send a client paging after
/// results we never promised.
/// </param>
public readonly record struct ReleasePage(IReadOnlyList<Release> Releases, int Offset, int Total)
{
    /// <summary>A page that is also the last one — the total is what we have returned.</summary>
    public static ReleasePage Last(IReadOnlyList<Release> releases, int offset) =>
        new(releases, offset, offset + releases.Count);
}

/// <summary>
/// Serves Newznab results from the catalog, resolving against the broadcasters on demand when a search term
/// has not been crawled yet (#58 / DR-011).
/// </summary>
/// <remarks>
/// <c>resolver</c> is optional, so a host that only reads the catalog needs no broadcaster clients wired in.
/// When it is absent the behaviour is exactly the pre-#58 read-only search.
/// </remarks>
public class SearchReleasesHandler(
    IEpisodeRepository episodes,
    OnDemandResolver? resolver = null,
    TvdbShowResolver? tvdbResolver = null)
{
    public async Task<ReleasePage> HandleAsync(SearchReleasesQuery query, CancellationToken ct = default)
    {
        var limit = Math.Clamp(query.Limit, 1, 500);

        // A TVDB id is the unambiguous question, so answer it directly when Sonarr asks it.
        if (query.TvdbId is not null)
        {
            // Resolve the id against TVDB and match backwards into our catalog. This is also where the
            // season/episode numbers come from — Sonarr always sends season=/ep=, and most Mediathek assets
            // carry only an air date, so without this a correctly mapped show still answers with nothing.
            if (tvdbResolver is not null)
            {
                var resolved = await tvdbResolver.ResolveAsync(query.TvdbId.Value, ct);
                var numbered = Project(resolved.Episodes, query, limit);
                if (numbered.Count > 0)
                    return ReleasePage.Last(numbered, query.Offset);
            }

            var byId = Project(await episodes.GetByTvdbIdAsync(query.TvdbId.Value, ct), query, limit);
            if (byId.Count > 0)
                return ReleasePage.Last(byId, query.Offset);

            // The id is one we have not mapped yet, and Sonarr sends **no title** alongside a tvdbid — so
            // there is nothing to fall through to. Rather than report "nothing exists" for what is our
            // own mapping gap, relax the query: for a daily search the air date alone identifies the
            // episode well enough, and Sonarr re-parses every title and discards other shows. That is
            // exactly how RSS-Sync already works against this indexer (#95).
            if (string.IsNullOrWhiteSpace(query.Q))
            {
                if (query.AirDate is { } airDate)
                    return ReleasePage.Last(
                        Project(await episodes.GetByBroadcastDateAsync(airDate, ct), query, limit),
                        query.Offset);

                return ReleasePage.Last(byId, query.Offset);
            }
        }

        // RSS (no query) is never resolved: RSS-Sync polls constantly with no particular target, so
        // resolving here would mean crawling on a timer for nothing specific. Per DR-011 it serves the
        // standing crawl list.
        if (string.IsNullOrWhiteSpace(query.Q))
        {
            // Paged in SQL rather than by fetching everything and discarding it — this is the whole
            // catalog, and a client catching up asks for the far end of it. The projection is then told
            // Offset 0, because the skipping already happened.
            var recent = await episodes.GetRecentAsync(query.Offset, limit, ct);

            return new ReleasePage(
                Project(recent, query with { Offset = 0 }, limit),
                query.Offset,
                await episodes.CountAsync(ct));
        }

        var matches = Project(await episodes.SearchAsync(query.Q!, ct), query, limit);
        if (matches.Count > 0 || resolver is null)
            return ReleasePage.Last(matches, query.Offset);

        // Nothing in the catalog. Ask the broadcasters, waiting only for the configured deadline — the
        // crawl continues in the background either way, so a later call gets the full set.
        if (await resolver.EnsureResolvedAsync(query.Q!, ct))
            return ReleasePage.Last(Project(await episodes.SearchAsync(query.Q!, ct), query, limit),
                query.Offset);

        // The deadline passed with the crawl still running. Return empty rather than an error: Sonarr
        // treats indexer errors as an availability problem and will disable an indexer that keeps failing,
        // so "no results yet" has to stay distinguishable from "broken".
        return ReleasePage.Last(matches, query.Offset);
    }

    private static List<Release> Project(IReadOnlyList<Episode> found, SearchReleasesQuery query, int limit)
    {
        IEnumerable<Episode> filtered = found;

        if (query.AirDate is { } airDate)
        {
            // Daily: match the date as *broadcast*, not as UTC. heute-show airs 20:30 Berlin, which is a
            // different UTC day for part of the year — comparing UTC would silently shift late-night
            // shows by one day and find nothing.
            filtered = filtered.Where(e => DateOnly.FromDateTime(e.BroadcastDate.DateTime) == airDate);
        }
        else if (query.SeasonOnly && query.Season is { } season)
        {
            // "season=2026" is a season number for a numbered show and a year for a dated one, and the
            // request alone cannot say which. Match either and let Sonarr's own parser discard the rest —
            // it re-parses every title regardless, so a superset costs noise, not correctness.
            filtered = filtered.Where(e =>
                e.SeasonNumber == season || e.BroadcastDate.Year == season);
        }
        else
        {
            if (query.Season is not null)
                filtered = filtered.Where(e => e.SeasonNumber == query.Season);
            if (query.Episode is not null)
                filtered = filtered.Where(e => e.EpisodeNumber == query.Episode);
        }

        return filtered.Skip(query.Offset).Take(limit).Select(ReleaseMapper.ToRelease).ToList();
    }

    /// <summary>
    /// Projects TVDB-resolved episodes, filtering on <b>TVDB's</b> numbering rather than the broadcaster's.
    /// </summary>
    /// <remarks>
    /// Sonarr asks in TVDB's terms, and the two disagree: measured on real KiKA data, our <c>S01/E27</c> is
    /// TVDB's <c>S2E1</c>. Filtering on our own numbers would miss the episode Sonarr asked for and,
    /// worse, occasionally return a different one under the requested number.
    /// </remarks>
    private static List<Release> Project(
        IReadOnlyList<NumberedEpisode> found,
        SearchReleasesQuery query,
        int limit)
    {
        IEnumerable<NumberedEpisode> filtered = found;

        // A dated show has no TVDB numbering to filter on either, so the air date is still the key.
        if (query.AirDate is { } airDate)
        {
            filtered = filtered.Where(e =>
                DateOnly.FromDateTime(e.Episode.BroadcastDate.DateTime) == airDate);
        }
        else if (query.SeasonOnly && query.Season is { } season)
        {
            filtered = filtered.Where(e => e.Season == season || e.Episode.BroadcastDate.Year == season);
        }
        else
        {
            if (query.Season is not null)
                filtered = filtered.Where(e => e.Season == query.Season);
            if (query.Episode is not null)
                filtered = filtered.Where(e => e.Number == query.Episode);
        }

        return filtered
            .Skip(query.Offset)
            .Take(limit)
            .Select(numbered => ReleaseMapper.ToRelease(numbered.Episode) with
            {
                TvdbId = numbered.TvdbId,
                Season = numbered.Season,
                Episode = numbered.Number,
                // Carry the id through the grab, so picking this release tells us which of the candidate
                // shows answers this TVDB id. The GUID stays the bare episode id — Sonarr dedups on it.
                DownloadToken = new ReleaseToken(numbered.Episode.Id, numbered.TvdbId).Encode(),
            })
            .ToList();
    }
}
