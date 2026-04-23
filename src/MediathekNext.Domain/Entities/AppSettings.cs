namespace MediathekNext.Domain.Entities;

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
}
