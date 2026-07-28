using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Enums;
using Krautwatch.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Krautwatch.Application.Indexing;

// ============================================================
// Result
// ============================================================

/// <summary>
/// One of our episodes, carrying the TVDB identity and numbering it should be published under.
/// </summary>
/// <param name="Season">
/// TVDB's season, or null when we could not number this episode. Null means the release falls back to
/// air-date naming, which Sonarr can only match for a series it models as daily.
/// </param>
public record NumberedEpisode(Episode Episode, int TvdbId, int? Season, int? Number);

/// <summary>Why a resolution produced what it did — for logging and for the settings UI.</summary>
public enum ResolutionOutcome
{
    /// <summary>Answered from a mapping we had already stored.</summary>
    AlreadyMapped = 0,

    /// <summary>Exactly one of our shows corroborated, so it was mapped automatically.</summary>
    AutoMapped = 1,

    /// <summary>Several of our shows corroborated; all were offered so the operator can pick.</summary>
    Candidates = 2,

    /// <summary>Nothing in our catalog corroborated the series.</summary>
    NoMatch = 3,

    /// <summary>TVDB is unconfigured or unreachable — matching degrades to titles.</summary>
    Unavailable = 4,
}

public record ResolutionResult(
    ResolutionOutcome Outcome,
    IReadOnlyList<NumberedEpisode> Episodes)
{
    public static readonly ResolutionResult Unavailable = new(ResolutionOutcome.Unavailable, []);
    public static readonly ResolutionResult NoMatch = new(ResolutionOutcome.NoMatch, []);
}

// ============================================================
// Action (IO-driven, DR-009)
// ============================================================

/// <summary>
/// Answers "Sonarr asked about TVDB id N — which of our shows is that, and how are its episodes numbered?"
/// </summary>
/// <remarks>
/// <para>
/// Matching runs <b>backwards</b>: Sonarr's episode query already carries the authoritative id, so we
/// resolve that id against TVDB and match into our catalog, rather than guessing forwards from a Mediathek
/// title. The same TVDB fetch yields the season/episode numbers Sonarr's mandatory <c>season=</c>/<c>ep=</c>
/// parameters need, so identity and numbering come from one round trip.
/// </para>
/// <para>
/// When several of our shows corroborate one id, <b>all</b> are returned rather than one being picked.
/// Sonarr's interactive search then acts as the disambiguation UI and the operator's grab is the answer —
/// far better than us guessing, because a wrong id is worse than no id: Sonarr trusts the id over the title
/// and would silently file episodes under the wrong series.
/// </para>
/// </remarks>
public class TvdbShowResolver(
    IShowMappingRepository mappings,
    IEpisodeRepository episodes,
    ITvdbCatalog tvdb,
    ILogger<TvdbShowResolver> logger)
{
    /// <summary>
    /// How many of our shows may be offered for one id. Keeps an interactive search readable; the cap is
    /// logged when it bites, because a silent truncation reads as "that's all there was".
    /// </summary>
    public const int MaxCandidates = 3;

    /// <summary>
    /// How many ranked shows are worth the corroboration round trip. Ranking is cheap and in-memory, but
    /// each check needs that show's episodes from the database.
    /// </summary>
    private const int MaxCorroborationChecks = 8;

    public async Task<ResolutionResult> ResolveAsync(int tvdbId, CancellationToken ct = default)
    {
        var stored = await mappings.GetByTvdbIdAsync(tvdbId, ct);
        if (stored.Count > 0)
            return new ResolutionResult(
                ResolutionOutcome.AlreadyMapped,
                await NumberAsync(tvdbId, stored.Select(m => m.ShowId), ct));

        if (!tvdb.IsConfigured)
            return ResolutionResult.Unavailable;

        var series = await tvdb.GetSeriesAsync(tvdbId, ct);
        if (series is null)
        {
            logger.LogDebug("TVDB {TvdbId} did not resolve; falling back to title matching", tvdbId);
            return ResolutionResult.Unavailable;
        }

        var tvdbEpisodes = await tvdb.GetEpisodesAsync(tvdbId, ct);
        if (tvdbEpisodes.Count == 0)
        {
            // Without an episode list there is nothing to corroborate against, and a name-only match is not
            // strong enough to persist — two unrelated shows can share a name.
            logger.LogDebug("TVDB {TvdbId} has no episode list; refusing to map on name alone", tvdbId);
            return ResolutionResult.NoMatch;
        }

        var ourShows = (await episodes.GetShowsAsync(ct: ct)).Select(row => row.Show).ToList();
        var ranked = ShowMatcher.Rank(series, ourShows);

        var corroborated = new List<CorroboratedCandidate>();
        foreach (var candidate in ranked.Take(MaxCorroborationChecks))
        {
            var ours = await episodes.GetByShowAsync(candidate.Show.Id, ct);
            var corroboration = EpisodeCorroboration.Check(ours, tvdbEpisodes);
            if (corroboration.IsCorroborated)
                corroborated.Add(new CorroboratedCandidate(candidate, corroboration, ours));
        }

        if (corroborated.Count == 0)
        {
            logger.LogInformation(
                "TVDB {TvdbId} \"{Name}\": {Ranked} name candidates, none corroborated by episodes",
                tvdbId, series.Name, ranked.Count);
            return ResolutionResult.NoMatch;
        }

        if (corroborated.Count == 1)
        {
            var (candidate, corroboration, ourEpisodes) = corroborated[0];
            await mappings.UpsertAsync(
                new ShowMapping
                {
                    TvdbId = tvdbId,
                    ShowId = candidate.Show.Id,
                    Provenance = MappingProvenance.Auto,
                    Evidence = $"{candidate.Evidence}; {corroboration.Matched}/{corroboration.Comparable} "
                             + "episodes corroborated",
                },
                ct);

            logger.LogInformation(
                "Mapped {ShowId} to TVDB {TvdbId} automatically ({Matched}/{Comparable} episodes)",
                candidate.Show.Id, tvdbId, corroboration.Matched, corroboration.Comparable);

            return new ResolutionResult(
                ResolutionOutcome.AutoMapped,
                Number(tvdbId, corroboration, ourEpisodes));
        }

        // Several plausible shows: offer them all and let the grab decide. Nothing is persisted here — a
        // mapping learned from an actual selection is worth more than one guessed from a ranking.
        if (corroborated.Count > MaxCandidates)
        {
            logger.LogInformation(
                "TVDB {TvdbId}: {Total} shows corroborated, offering the top {Cap}",
                tvdbId, corroborated.Count, MaxCandidates);
        }

        var offered = corroborated.Take(MaxCandidates).ToList();
        logger.LogInformation(
            "TVDB {TvdbId} \"{Name}\" is ambiguous across {Count} of our shows: {Shows}",
            tvdbId, series.Name, offered.Count,
            string.Join(", ", offered.Select(o => o.Candidate.Show.Id)));

        return new ResolutionResult(
            ResolutionOutcome.Candidates,
            offered.SelectMany(o => Number(tvdbId, o.Corroboration, o.OurEpisodes)).ToList());
    }

    /// <summary>A ranked show whose episodes agree with the TVDB series, kept together with the evidence.</summary>
    private record CorroboratedCandidate(
        ShowCandidate Candidate,
        CorroborationResult Corroboration,
        IReadOnlyList<Episode> OurEpisodes);

    /// <summary>Numbers the episodes of already-mapped shows.</summary>
    private async Task<IReadOnlyList<NumberedEpisode>> NumberAsync(
        int tvdbId,
        IEnumerable<string> showIds,
        CancellationToken ct)
    {
        var tvdbEpisodes = tvdb.IsConfigured
            ? await tvdb.GetEpisodesAsync(tvdbId, ct)
            : [];

        var results = new List<NumberedEpisode>();
        foreach (var showId in showIds)
        {
            var ours = await episodes.GetByShowAsync(showId, ct);
            if (tvdbEpisodes.Count == 0)
            {
                // Mapped but unnumberable (TVDB down or unconfigured). Still emit the releases with the id —
                // Sonarr can at least identify the series, which beats returning nothing.
                results.AddRange(ours.Select(e => new NumberedEpisode(e, tvdbId, null, null)));
                continue;
            }

            results.AddRange(Number(tvdbId, EpisodeCorroboration.Check(ours, tvdbEpisodes), ours));
        }

        return results;
    }

    private static List<NumberedEpisode> Number(
        int tvdbId,
        CorroborationResult corroboration,
        IReadOnlyList<Episode> all)
    {
        var byId = corroboration.Numbering.ToDictionary(n => n.EpisodeId, StringComparer.Ordinal);

        // Episodes the corroboration matched carry TVDB numbering. Unmatched ones are still ours and still
        // worth publishing under the id, just without numbers.
        return all
            .Select(episode => byId.TryGetValue(episode.Id, out var numbering)
                ? new NumberedEpisode(episode, tvdbId, numbering.Season, numbering.Number)
                : new NumberedEpisode(episode, tvdbId, null, null))
            .ToList();
    }
}
