namespace MediathekNext.Domain.Entities;

public class AppSettings
{
    public string DownloadDirectory { get; set; } = "/downloads";
    public int MaxConcurrentDownloads { get; set; } = 2;
    public int CatalogRefreshIntervalHours { get; set; } = 6;
    public string CatalogProviderKey { get; set; } = "mediathekview";
}
