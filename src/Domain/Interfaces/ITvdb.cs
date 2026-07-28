namespace Krautwatch.Domain.Interfaces;

/// <summary>
/// Read boundary to TheTVDB.
/// </summary>
/// <remarks>
/// <para>
/// The primary direction is <b>id → metadata</b>, not title → search. Sonarr's episode query already
/// carries the authoritative <c>tvdbid</c>, so resolving that id and matching backwards into our catalog
/// is strictly better than guessing forwards from a Mediathek title. <see cref="SearchAsync"/> exists only
/// for the title-only paths (a <c>q=</c> query with no id, our own Web UI, bulk pre-warming).
/// </para>
/// <para>
/// Every member is allowed to return null/empty rather than throw when TVDB is unreachable or unconfigured.
/// A missing API key must degrade matching, never break search: Sonarr disables an indexer that keeps
/// erroring, so "no answer" has to stay distinguishable from "broken".
/// </para>
/// </remarks>
public interface ITvdbCatalog
{
    /// <summary>True when a usable API key is configured. False means every call here returns nothing.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// True when the key came from configuration (environment variable / user-secrets) rather than the
    /// database. Configuration wins, so the settings UI shows the key as managed elsewhere and read-only —
    /// letting the UI appear to change a value that config will keep overriding is worse than saying so.
    /// </summary>
    bool IsKeyFromConfiguration { get; }

    /// <summary>The series record for an id, including the German translation and aliases, or null.</summary>
    Task<TvdbSeries?> GetSeriesAsync(int tvdbId, CancellationToken ct = default);

    /// <summary>
    /// The series' episodes in its default season-type ordering. Used to turn our air dates into the
    /// (season, episode) pair Sonarr insists on — most German public-TV content is dated, not numbered.
    /// </summary>
    Task<IReadOnlyList<TvdbEpisode>> GetEpisodesAsync(int tvdbId, CancellationToken ct = default);

    /// <summary>
    /// Title search, restricted to German-country records. The country filter is what makes this usable:
    /// unfiltered, <c>Das Traumschiff</c> returns Star Trek and <c>Panorama</c> returns the BBC.
    /// </summary>
    Task<IReadOnlyList<TvdbSeries>> SearchAsync(string title, CancellationToken ct = default);
}

/// <summary>
/// A TVDB series, reduced to the fields that matter for matching.
/// </summary>
/// <param name="Aliases">
/// Alternate and translated names. These carry most of the matching value — TVDB's *primary* name is
/// often the English one (tvdb 73518 is "Maya the Bee", not "Die Biene Maja"), so comparing primary
/// titles alone would miss the German name that the Mediathek actually uses.
/// </param>
/// <param name="Network">
/// The broadcaster. Note ARD is a federation, so this is usually the regional member ("Norddeutscher
/// Rundfunk (NDR)" for tvdb 255986) rather than "Das Erste" — filtering on the national brand alone
/// would drop real matches.
/// </param>
public record TvdbSeries(
    int TvdbId,
    string Name,
    int? Year,
    string? Network,
    IReadOnlyList<string> Aliases)
{
    /// <summary>Primary name plus every alias — the full set to match a Mediathek title against.</summary>
    public IEnumerable<string> AllNames => new[] { Name }.Concat(Aliases);
}

/// <summary>
/// One TVDB episode's numbering and air date. <paramref name="AirDate"/> is the join key onto our
/// <c>Episode.BroadcastDate</c>; it is nullable because unaired/TBA entries carry no date and must not
/// be matched against anything.
/// </summary>
public record TvdbEpisode(int Season, int Number, DateOnly? AirDate, string? Name);
