using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Interfaces;
using Krautwatch.Domain.ValueObjects;

namespace Krautwatch.Application.Indexing;

/// <summary>How one of our episodes was tied to a TVDB episode.</summary>
public enum MatchedBy
{
    /// <summary>Episode titles agreed after normalisation — the strongest signal available.</summary>
    Title = 0,

    /// <summary>Air dates agreed exactly.</summary>
    AirDate = 1,

    /// <summary>Air dates agreed within <see cref="EpisodeCorroboration.ToleranceDays"/>.</summary>
    NearAirDate = 2,
}

/// <summary>
/// The (season, episode) TVDB gives one of our episodes.
/// </summary>
/// <remarks>
/// These are always <b>TVDB's</b> numbers, never the broadcaster's. Measured on real KiKA data, our own
/// numbering disagrees with TVDB for the very same episode — our <c>S01/E27</c> is TVDB's <c>S2E1</c> and
/// our <c>S02/E43</c> is TVDB's <c>S4E43</c>. Sonarr matches against TVDB, so emitting the broadcaster's
/// numbers would file episodes under the wrong season.
/// </remarks>
public record EpisodeNumbering(string EpisodeId, int Season, int Number, MatchedBy MatchedBy);

/// <summary>
/// How well our episodes line up with a TVDB series' episode list, plus the numbering that fell out of it.
/// </summary>
public record CorroborationResult(
    int Matched,
    int Comparable,
    IReadOnlyList<EpisodeNumbering> Numbering)
{
    public static readonly CorroborationResult None = new(0, 0, []);

    public double Ratio => Comparable == 0 ? 0 : (double)Matched / Comparable;

    /// <summary>
    /// True when the overlap is real rather than coincidental.
    /// </summary>
    /// <remarks>
    /// Two thresholds, because either alone fails: a bare count lets one coincidental hit validate a wrong
    /// series, while a bare ratio treats one episode matching one episode as certainty.
    /// </remarks>
    public bool IsCorroborated =>
        Comparable > 0
        && Ratio >= EpisodeCorroboration.MinimumRatio
        && (Matched >= EpisodeCorroboration.MinimumMatches || Matched == Comparable);
}

/// <summary>
/// Ties our episodes to a TVDB series' episode list, by title first and air date second.
/// </summary>
/// <remarks>
/// <para>
/// This does two jobs with one mechanism. It <b>corroborates</b> a candidate show mapping — the two
/// <i>Biene Maja</i> series share the name "Die Biene Maja", so no name comparison can separate them, but
/// our catalog agrees with the 2013 series' episode list on all 48 episodes and with the 1975 series on
/// none. And it <b>numbers</b> our episodes, which Sonarr's mandatory <c>season=</c>/<c>ep=</c> parameters
/// require and which is what lets us emit <c>SxxEyy</c> titles at all.
/// </para>
/// <para>
/// <b>Why title before date.</b> Measured against the live API for <c>Die Biene Maja</c>: episode-title
/// matching identified 48/48, our own season/episode numbers 19/48, and exact air date only 12/48. KiKA
/// broadcasts that series as re-runs, so its <c>BroadcastDate</c> is a re-airing rather than the original —
/// TVDB has "Knacks im Schneckenhaus" on 2013-04-03 where KiKA aired it on 2013-04-28. Dated topical shows
/// are the mirror image (heute-show 28/28, extra 3 15/15, ZDF Magazin Royale 16/16 all match on date), so
/// both passes are needed and neither alone suffices.
/// </para>
/// <para>
/// It is a corroborator, never a discriminator on its own: two unrelated weekly shows on one channel share
/// an airing pattern, so this must always follow <see cref="ShowMatcher"/> having established that the
/// names plausibly correspond.
/// </para>
/// </remarks>
public static class EpisodeCorroboration
{
    public const int MinimumMatches = 2;
    public const double MinimumRatio = 0.34;

    /// <summary>
    /// Broadcast slots straddle midnight and TVDB records the nominal air date, so exact equality alone
    /// would drop legitimate matches. One day either side absorbs that without letting an unrelated
    /// schedule line up.
    /// </summary>
    public const int ToleranceDays = 1;

    public static CorroborationResult Check(
        IEnumerable<Episode> ourEpisodes,
        IEnumerable<TvdbEpisode> tvdbEpisodes)
    {
        var tvdb = tvdbEpisodes.ToList();
        if (tvdb.Count == 0)
            return CorroborationResult.None;

        // Index by title and by date. TryAdd keeps the first of any duplicate key: TVDB does carry repeated
        // episode names, and inventing a tie-break between them would be guessing.
        var byTitle = new Dictionary<string, TvdbEpisode>(StringComparer.Ordinal);
        var byDate = new Dictionary<DateOnly, TvdbEpisode>();
        foreach (var episode in tvdb)
        {
            var title = TitleNormalizer.NormalizeEpisodeTitle(episode.Name);
            if (title.Length > 0)
                byTitle.TryAdd(title, episode);

            if (episode.AirDate is { } date)
                byDate.TryAdd(date, episode);
        }

        var ours = ourEpisodes
            .Select(episode => (
                episode.Id,
                Title: TitleNormalizer.NormalizeEpisodeTitle(episode.Title),
                // Local German date: the broadcast date identifies a Mediathek asset, and converting to UTC
                // would shift late-evening airings onto the previous day.
                Date: DateOnly.FromDateTime(episode.BroadcastDate.DateTime)))
            .ToList();

        if (ours.Count == 0)
            return CorroborationResult.None;

        // A TVDB episode may be claimed once only, or two of our assets would carry the same SxxEyy and
        // Sonarr would see duplicate releases for one episode. Passes run strongest-signal-first across the
        // whole set rather than per episode, so a weaker claim can never steal an episode from a stronger
        // one that happens to appear later in the list.
        var claimed = new HashSet<(int Season, int Number)>();
        var numbering = new Dictionary<string, EpisodeNumbering>(StringComparer.Ordinal);

        Pass(ours, numbering, claimed, MatchedBy.Title,
            (item, _) => byTitle.TryGetValue(item.Title, out var hit) ? hit : null);

        Pass(ours, numbering, claimed, MatchedBy.AirDate,
            (item, _) => byDate.TryGetValue(item.Date, out var hit) ? hit : null);

        Pass(ours, numbering, claimed, MatchedBy.NearAirDate,
            (item, taken) => FindNearby(byDate, taken, item.Date));

        return new CorroborationResult(numbering.Count, ours.Count, numbering.Values.ToList());
    }

    private static void Pass(
        List<(string Id, string Title, DateOnly Date)> ours,
        Dictionary<string, EpisodeNumbering> numbering,
        HashSet<(int Season, int Number)> claimed,
        MatchedBy matchedBy,
        Func<(string Id, string Title, DateOnly Date), HashSet<(int Season, int Number)>, TvdbEpisode?> find)
    {
        foreach (var item in ours)
        {
            if (numbering.ContainsKey(item.Id))
                continue;

            if (find(item, claimed) is not { } hit)
                continue;

            if (!claimed.Add((hit.Season, hit.Number)))
                continue;

            numbering[item.Id] = new EpisodeNumbering(item.Id, hit.Season, hit.Number, matchedBy);
        }
    }

    private static TvdbEpisode? FindNearby(
        Dictionary<DateOnly, TvdbEpisode> byDate,
        HashSet<(int Season, int Number)> claimed,
        DateOnly ourDate)
    {
        for (var offset = 1; offset <= ToleranceDays; offset++)
        {
            foreach (var candidate in new[] { ourDate.AddDays(-offset), ourDate.AddDays(offset) })
            {
                if (byDate.TryGetValue(candidate, out var hit) && !claimed.Contains((hit.Season, hit.Number)))
                    return hit;
            }
        }

        return null;
    }
}
