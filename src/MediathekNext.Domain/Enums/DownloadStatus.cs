namespace MediathekNext.Domain.Enums;

public enum DownloadStatus
{
    Queued = 0,
    Downloading = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4
}
