// TODO: Add TickerQ integration — using TickerQ.Utilities.Base; using TickerQ.Utilities.Interfaces.Managers;

namespace MediathekNext.Infrastructure.Jobs;

/// <summary>
/// Phase 1 of the download chain.
///
/// Responsibilities:
///   - Mark DownloadJob as Resolving
///   - HTTP HEAD the stream URL to verify it's reachable
///   - Detect stream type: HLS (.m3u8) or direct MP4
///   - Capture Content-Length if available (improves download progress accuracy)
///   - On success: update DownloadJob + enqueue Phase 2 (DownloadStreamJob)
///   - On failure: throw — TickerQ retries up to 3 times (10s / 30s / 60s backoff)
///   - On exhaustion: mark DownloadJob.Status = UrlUnavailable
///
/// Retry policy is configured in TickerQSeedService / StartDownloadHandler.
/// </summary>
// TODO (TickerQ): constructor will need AppDbContext, FileNamingService, IEpisodeRepository,
//                 ISettingsRepository, ITimeTickerManager, HttpClient, ILogger
public class ResolveStreamJob
{
    public const string FunctionName = "ResolveStream";

    // TODO: Wire up TickerQ — uncomment [TickerFunction], add ITimeTickerManager parameter,
    //       restore TickerFunctionContext<DownloadJobContext> parameter, and implement body.
    public async Task RunAsync(CancellationToken ct)
    {
        await Task.CompletedTask; // stub — implementation pending TickerQ integration
    }

    private static string DetectStreamType(string url, string contentType)
    {
        if (url.Contains(".m3u8") ||
            contentType.Contains("mpegurl") ||
            contentType.Contains("x-mpegURL"))
            return "HLS";

        return "MP4";
    }
}
