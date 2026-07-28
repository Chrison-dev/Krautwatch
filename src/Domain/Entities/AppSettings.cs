using Krautwatch.Domain.Enums;

namespace Krautwatch.Domain.Entities;

/// <summary>
/// Singleton settings row — always Id = 1 in the database.
/// </summary>
public class AppSettings
{
    public int Id { get; set; } = 1;
    public string DownloadDirectory { get; set; } = "/downloads";
    public int MaxConcurrentDownloads { get; set; } = 2;
    public int CatalogRefreshIntervalHours { get; set; } = 6;
    public string CatalogProviderKey { get; set; } = "mediathekview";

    /// <summary>
    /// What a search should do when the show has not been crawled yet (#58). Defaults to
    /// <see cref="SearchWaitMode.ReturnFast"/>, because Sonarr treats a slow indexer as a broken one.
    /// </summary>
    public SearchWaitMode SearchWaitMode { get; set; } = SearchWaitMode.ReturnFast;

    /// <summary>
    /// How many seconds a search waits before answering, when
    /// <see cref="SearchWaitMode"/> is <see cref="Enums.SearchWaitMode.ReturnFast"/>. Advanced setting —
    /// ignored entirely in <see cref="Enums.SearchWaitMode.WaitForComplete"/> mode.
    /// </summary>
    public int SearchWaitSeconds { get; set; } = 8;

    /// <summary>
    /// TheTVDB API key, when the operator supplied one through the settings UI rather than configuration.
    /// </summary>
    /// <remarks>
    /// Configuration (<c>TvdbConfiguration:ApiKey</c> — environment variable or user-secrets) takes
    /// precedence over this: an operator who sets an env var in a compose file expects it to apply, and
    /// being silently overridden by a stale row from an earlier UI edit is a bad debugging experience. This
    /// exists so a plain install can be configured entirely from the UI.
    ///
    /// Stored in plain text today, like the *arr instance keys — see #60 for encrypting secrets at rest.
    /// </remarks>
    public string? TvdbApiKey { get; set; }
}
