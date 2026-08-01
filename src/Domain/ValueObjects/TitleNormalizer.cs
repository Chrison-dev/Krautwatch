using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Krautwatch.Domain.ValueObjects;


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
