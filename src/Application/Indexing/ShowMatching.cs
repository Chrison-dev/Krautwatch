using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Interfaces;

namespace Krautwatch.Application.Indexing;

// ============================================================
// Title normalisation
// ============================================================

/// <summary>
/// Folds a Mediathek or TVDB title down to a comparable form.
/// </summary>
/// <remarks>
/// Only used for <i>our own</i> comparisons. Do not normalise before querying TVDB's search endpoint —
/// measured against the live API, that search is already case- and punctuation-insensitive
/// (<c>extra 3</c> matches, <c>heute show</c> ≡ <c>heute-show</c>), so pre-mangling the query buys
/// nothing and can lose signal.
/// </remarks>
public static partial class TitleNormalizer
{
    /// <summary>
    /// Lower-cases, folds German umlauts and other diacritics, drops a trailing parenthetical or bare
    /// year, and reduces everything else to single-spaced alphanumerics.
    /// </summary>
    /// <remarks>
    /// The year has to go because TVDB disambiguates in the title itself — tvdb 266275 is literally named
    /// "Die Biene Maja (2013)" while the Mediathek just says "Die Biene Maja". Dropping it is what lets the
    /// two compare equal; the *year* discrimination is then done properly by
    /// <see cref="AirDateCorroboration"/>, which uses evidence rather than a string.
    /// </remarks>
    public static string Normalize(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        var folded = FoldDiacritics(title.ToLowerInvariant());
        var withoutYear = TrailingYearRegex().Replace(folded, " ");
        var alphanumeric = NonAlphanumericRegex().Replace(withoutYear, " ");
        return WhitespaceRegex().Replace(alphanumeric, " ").Trim();
    }

    /// <summary>
    /// Folds an <i>episode</i> title for comparison against TVDB's episode names.
    /// </summary>
    /// <remarks>
    /// Same folding as <see cref="Normalize"/> but it first strips the broadcaster's inline numbering
    /// marker: KiKA titles episodes "Knacks im Schneckenhaus (S01/E08)" where TVDB simply says "Knacks im
    /// Schneckenhaus". It deliberately does <b>not</b> strip a trailing year — dated topical shows are named
    /// "heute-show vom 5. Juni 2026" on both sides, and that date is the whole identity of the episode.
    /// </remarks>
    public static string NormalizeEpisodeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        var withoutMarker = NumberingMarkerRegex().Replace(title, " ");
        var folded = FoldDiacritics(withoutMarker.ToLowerInvariant());
        var alphanumeric = NonAlphanumericRegex().Replace(folded, " ");
        return WhitespaceRegex().Replace(alphanumeric, " ").Trim();
    }

    /// <summary>
    /// German definite article stripped from the front, or null when there is none.
    /// </summary>
    /// <remarks>
    /// This is a <b>fallback</b>, never an unconditional rule. Measured against TVDB search, most
    /// article-prefixed titles match fine with the article present ("Die Biene Maja", "Der Tatortreiniger",
    /// "Die Anstalt", "Das Traumschiff"), so stripping always would discard a working query. But
    /// "Die Sendung mit der Maus" returns nothing while "Sendung mit der Maus" returns tvdb 153241 — so on
    /// an empty result it is worth one retry.
    /// </remarks>
    public static string? WithoutLeadingArticle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var match = LeadingArticleRegex().Match(title.Trim());
        return match.Success ? match.Groups["rest"].Value : null;
    }

    // Decompose, drop combining marks, then map the German pairs that do not decompose to a bare letter.
    private static string FoldDiacritics(string value)
    {
        var expanded = value
            .Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue")
            .Replace("ß", "ss");

        var decomposed = expanded.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                builder.Append(ch);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    [GeneratedRegex(@"\s*\(?\b(19|20)\d{2}\b\)?\s*$")]
    private static partial Regex TrailingYearRegex();

    // The Mediathek's inline numbering, e.g. "(S01/E08)" or "(S1/E8)".
    [GeneratedRegex(@"\(\s*S\d+\s*/\s*E\d+\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex NumberingMarkerRegex();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonAlphanumericRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^(der|die|das)\s+(?<rest>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingArticleRegex();
}

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
/// deliberately not the final word: <see cref="AirDateCorroboration"/> is what turns a ranked guess into
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
