using System.Globalization;

namespace Krautwatch.Application.Indexing;

/// <summary>
/// Interprets Newznab's <c>season</c>/<c>ep</c> pair, which encodes three different regimes in two
/// untyped parameters (#95).
/// </summary>
/// <remarks>
/// <para>
/// Measured against Sonarr 4.0.19, which sends:
/// </para>
/// <list type="bullet">
///   <item><c>season=1&amp;ep=10</c> — standard, both plain numbers.</item>
///   <item><c>season=2026&amp;ep=06/05</c> — <b>daily</b>: season is the <i>year</i> and ep is
///   <c>MM/DD</c>. Together they are an air date, not a number.</item>
///   <item><c>season=2026</c> with no <c>ep</c> — a whole-season search, which for a daily series means
///   a whole <i>year</i>.</item>
/// </list>
/// <para>
/// The shape of the request is enough to tell these apart: a slash in <c>ep</c> is unambiguous, because
/// no numbering regime produces one. So the regime is read off the query rather than looked up per show
/// — no database round-trip, and no state to keep in step with the broadcasters.
/// </para>
/// <para>
/// <b>Never rejects.</b> An unparseable value degrades to "no constraint" rather than an error: this runs
/// on Sonarr's request path, and Sonarr disables an indexer that keeps failing. Before this existed,
/// <c>ep</c> was bound as an <c>int</c> and <c>ep=06/05</c> produced a 400 on every daily search.
/// </para>
/// </remarks>
public readonly record struct NewznabEpisodeQuery(int? Season, int? Episode, DateOnly? AirDate)
{
    /// <summary>True when the caller asked for a whole season/year rather than one episode.</summary>
    public bool IsSeasonOnly => Season is not null && Episode is null && AirDate is null;

    public static NewznabEpisodeQuery Parse(string? season, string? ep)
    {
        var seasonNumber = TryInt(season);
        var trimmed = ep?.Trim();

        if (string.IsNullOrEmpty(trimmed))
            return new NewznabEpisodeQuery(seasonNumber, null, null);

        // Daily: "MM/DD", with the year carried in `season`. Also accepts "M/D".
        var slash = trimmed.IndexOf('/');
        if (slash > 0 && seasonNumber is { } year)
        {
            var month = TryInt(trimmed[..slash]);
            var day = TryInt(trimmed[(slash + 1)..]);

            if (month is { } m && day is { } d && IsRealDate(year, m, d))
                return new NewznabEpisodeQuery(year, null, new DateOnly(year, m, d));
        }

        // Standard or absolute numbering.
        return new NewznabEpisodeQuery(seasonNumber, TryInt(trimmed), null);
    }

    private static bool IsRealDate(int year, int month, int day) =>
        year is >= 1900 and <= 2999
        && month is >= 1 and <= 12
        && day >= 1
        && day <= DateTime.DaysInMonth(year, month);

    private static int? TryInt(string? value) =>
        int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
}
