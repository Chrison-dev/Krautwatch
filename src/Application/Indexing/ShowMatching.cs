using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Interfaces;
using Krautwatch.Domain.ValueObjects;

namespace Krautwatch.Application.Indexing;

// ============================================================
// Candidate ranking
// ============================================================

/// <summary>How strongly one of our shows resembles a TVDB series, and why.</summary>
public record ShowCandidate(Show Show, int Score, string Evidence)
{
    /// <summary>
    /// A name matched exactly after normalisation. Necessary but not sufficient for auto-mapping — the two
    /// <i>Biene Maja</i> series share the name "Die Biene Maja", so an exact name hit alone cannot tell
    /// 1975 from 2013.
    /// </summary>
    public bool IsExact => Score >= ShowMatcher.ExactScore;
}

/// <summary>
/// Ranks our catalog against a resolved TVDB series. Pure name comparison — deliberately no IO, and
/// deliberately not the final word: <see cref="EpisodeCorroboration"/> is what turns a ranked guess into
/// something worth persisting.
/// </summary>
public static class ShowMatcher
{
    public const int ExactScore = 100;
    private const int PrefixScore = 70;
    private const int ContainsScore = 45;
    private const int BroadcasterBonus = 10;

    /// <summary>Below this a resemblance is noise and is not offered at all.</summary>
    public const int MinimumScore = ContainsScore;

    /// <summary>
    /// ARD is a federation: TVDB credits the regional member ("Norddeutscher Rundfunk (NDR)") far more
    /// often than the national brand, so an ARD-channel show must be allowed to agree with any member.
    /// </summary>
    private static readonly string[] ArdMembers =
        ["ard", "das erste", "ndr", "wdr", "br", "swr", "mdr", "hr", "rbb", "sr", "norddeutscher",
         "westdeutscher", "bayerischer", "suedwest", "mitteldeutscher", "hessischer", "saarlaendischer"];

    /// <summary>
    /// Our shows that plausibly correspond to <paramref name="series"/>, best first. Ties break on the
    /// show id so the ordering is deterministic — an unattended grab must not depend on enumeration order.
    /// </summary>
    public static IReadOnlyList<ShowCandidate> Rank(TvdbSeries series, IEnumerable<Show> ourShows)
    {
        var tvdbNames = series.AllNames
            .Select(TitleNormalizer.Normalize)
            .Where(name => name.Length > 0)
            .Distinct()
            .ToList();

        if (tvdbNames.Count == 0)
            return [];

        return ourShows
            .Select(show => Score(show, series, tvdbNames))
            .Where(candidate => candidate is not null && candidate.Score >= MinimumScore)
            .Select(candidate => candidate!)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Show.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static ShowCandidate? Score(Show show, TvdbSeries series, List<string> tvdbNames)
    {
        var ours = TitleNormalizer.Normalize(show.Title);
        if (ours.Length == 0)
            return null;

        var best = 0;
        var matchedOn = string.Empty;

        foreach (var tvdbName in tvdbNames)
        {
            var score = Compare(ours, tvdbName);
            if (score > best)
            {
                best = score;
                matchedOn = tvdbName;
            }
        }

        if (best == 0)
            return null;

        var agrees = BroadcasterAgrees(show.ChannelId, series.Network);
        var total = best + (agrees ? BroadcasterBonus : 0);
        var evidence = $"TVDB {series.TvdbId} \"{series.Name}\" via \"{matchedOn}\""
                     + (agrees ? $"; broadcaster agrees ({series.Network})" : string.Empty);

        return new ShowCandidate(show, total, evidence);
    }

    private static int Compare(string ours, string theirs)
    {
        if (ours == theirs)
            return ExactScore;

        // "extra 3 der irrsinn der woche" against "extra 3" — the Mediathek habitually appends a strand
        // subtitle to the on-air brand, so a prefix is strong evidence rather than a coincidence.
        if (ours.StartsWith(theirs + " ", StringComparison.Ordinal))
            return PrefixScore;
        if (theirs.StartsWith(ours + " ", StringComparison.Ordinal))
            return PrefixScore;

        // Require a word boundary: bare substring matching makes "extra 3" match "3-2-1 contact extra".
        if (ContainsWord(ours, theirs) || ContainsWord(theirs, ours))
            return ContainsScore;

        return 0;
    }

    private static bool ContainsWord(string haystack, string needle) =>
        needle.Length > 0 && $" {haystack} ".Contains($" {needle} ", StringComparison.Ordinal);

    private static bool BroadcasterAgrees(string? channelId, string? network)
    {
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(network))
            return false;

        var ours = channelId.ToLowerInvariant();
        var theirs = network.ToLowerInvariant();

        if (theirs.Contains(ours, StringComparison.Ordinal))
            return true;

        // ARD's members count as ARD; every other channel id compares literally.
        return ours is "ard" && ArdMembers.Any(member => theirs.Contains(member, StringComparison.Ordinal));
    }
}
