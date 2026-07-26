using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Interfaces;

namespace Krautwatch.Application.Indexing;

/// <summary>
/// A Newznab search (t=search / t=tvsearch) or, when <see cref="Q"/> is empty, the RSS feed of the
/// most recent releases. Optional season/episode narrow a Standard-series query.
/// </summary>
public record SearchReleasesQuery(string? Q = null, int? Season = null, int? Episode = null, int Limit = 100);

public class SearchReleasesHandler(IEpisodeRepository episodes)
{
    public async Task<IReadOnlyList<Release>> HandleAsync(SearchReleasesQuery query, CancellationToken ct = default)
    {
        var limit = Math.Clamp(query.Limit, 1, 500);

        var found = string.IsNullOrWhiteSpace(query.Q)
            ? await episodes.GetRecentAsync(limit, ct)
            : await episodes.SearchAsync(query.Q!, ct);

        IEnumerable<Episode> filtered = found;
        if (query.Season is not null)
            filtered = filtered.Where(e => e.SeasonNumber == query.Season);
        if (query.Episode is not null)
            filtered = filtered.Where(e => e.EpisodeNumber == query.Episode);

        return filtered.Take(limit).Select(ReleaseMapper.ToRelease).ToList();
    }
}
