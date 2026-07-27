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
}
