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
    int? TvdbId = null);

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
    public async Task<IReadOnlyList<Release>> HandleAsync(SearchReleasesQuery query, CancellationToken ct = default)
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
                    return numbered;
            }

            var byId = Project(await episodes.GetByTvdbIdAsync(query.TvdbId.Value, ct), query, limit);
            if (byId.Count > 0)
                return byId;

            // The id is one we have not mapped yet. Fall through to the title search when Sonarr also sent
            // one, rather than reporting "nothing exists" — an unmapped show is our gap, not an absent show.
            if (string.IsNullOrWhiteSpace(query.Q))
                return byId;
        }

        // RSS (no query) is never resolved: RSS-Sync polls constantly with no particular target, so
        // resolving here would mean crawling on a timer for nothing specific. Per DR-011 it serves the
        // standing crawl list.
        if (string.IsNullOrWhiteSpace(query.Q))
            return Project(await episodes.GetRecentAsync(limit, ct), query, limit);

        var matches = Project(await episodes.SearchAsync(query.Q!, ct), query, limit);
        if (matches.Count > 0 || resolver is null)
            return matches;

        // Nothing in the catalog. Ask the broadcasters, waiting only for the configured deadline — the
        // crawl continues in the background either way, so a later call gets the full set.
        if (await resolver.EnsureResolvedAsync(query.Q!, ct))
            return Project(await episodes.SearchAsync(query.Q!, ct), query, limit);

        // The deadline passed with the crawl still running. Return empty rather than an error: Sonarr
        // treats indexer errors as an availability problem and will disable an indexer that keeps failing,
        // so "no results yet" has to stay distinguishable from "broken".
        return matches;
    }

    private static List<Release> Project(IReadOnlyList<Episode> found, SearchReleasesQuery query, int limit)
    {
        IEnumerable<Episode> filtered = found;
        if (query.Season is not null)
            filtered = filtered.Where(e => e.SeasonNumber == query.Season);
        if (query.Episode is not null)
            filtered = filtered.Where(e => e.EpisodeNumber == query.Episode);

        return filtered.Take(limit).Select(ReleaseMapper.ToRelease).ToList();
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
        if (query.Season is not null)
            filtered = filtered.Where(e => e.Season == query.Season);
        if (query.Episode is not null)
            filtered = filtered.Where(e => e.Number == query.Episode);

        return filtered
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
