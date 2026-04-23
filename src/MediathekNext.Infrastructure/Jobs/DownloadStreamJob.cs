using System.Diagnostics;
using System.Text.RegularExpressions;
using MediathekNext.Domain.Enums;
using MediathekNext.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
// TODO: Add TickerQ integration — using TickerQ.Utilities.Base; using TickerQ.Utilities.Interfaces.Managers;

namespace MediathekNext.Infrastructure.Jobs;

/// <summary>
/// Phase 2 of the download chain.
///
/// Responsibilities:
///   - Run ffmpeg: stream URL → temp path (-c copy, no transcoding)
///   - Track progress via ffmpeg stderr time= output
///   - Write to .part temp file — never to final path until Finalise
///   - On success: mark DownloadJob as Downloaded + enqueue Phase 3
///   - On failure: throw — TickerQ retries (60s / 120s / 300s / 600s backoff)
///   - On exhaustion: MarkDownloadFailed — temp file preserved for inspection
/// </summary>
public partial class DownloadStreamJob(
    AppDbContext db,
    /* ITimeTickerManager timeTickerManager, */ // TODO: Implement TickerQ
    ILogger<DownloadStreamJob> logger)
{
    public const string FunctionName = "DownloadStream";

    // Matches ffmpeg stderr progress lines: time=00:12:34.56
    [GeneratedRegex(@"time=(\d{2}):(\d{2}):(\d{2})\.(\d{2})", RegexOptions.Compiled)]
    private static partial Regex TimePattern();

    // TODO: Wire up TickerQ — uncomment [TickerFunction], add ITimeTickerManager parameter,
    //       restore TickerFunctionContext<DownloadJobContext> parameter, and implement body.
    public async Task RunAsync(CancellationToken ct)
    {
        await Task.CompletedTask; // stub — implementation pending TickerQ integration
    }

    private async Task RunFfmpegAsync(
        string args,
        TimeSpan? knownDuration,
        Domain.Entities.DownloadJob job,
        CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName               = "ffmpeg",
                Arguments              = args,
                RedirectStandardError  = true,
                RedirectStandardOutput = false,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            }
        };

        process.Start();

        // Read stderr on background task to avoid deadlock
        var stderrTask = ReadProgressAsync(
            process.StandardError,
            knownDuration,
            job,
            ct);

        await process.WaitForExitAsync(ct);
        await stderrTask;

        if (process.ExitCode != 0)
            throw new Exception(
                $"ffmpeg exited with code {process.ExitCode} for Job={job.Id}");
    }

    private async Task ReadProgressAsync(
        StreamReader stderr,
        TimeSpan? knownDuration,
        Domain.Entities.DownloadJob job,
        CancellationToken ct)
    {
        string? line;
        while ((line = await stderr.ReadLineAsync(ct)) is not null)
        {
            var match = TimePattern().Match(line);
            if (!match.Success || knownDuration is null) continue;

            var h   = int.Parse(match.Groups[1].Value);
            var m   = int.Parse(match.Groups[2].Value);
            var s   = int.Parse(match.Groups[3].Value);
            var cs  = int.Parse(match.Groups[4].Value);
            var pos = new TimeSpan(0, h, m, s, cs * 10);

            var pct = Math.Min(99.0, pos.TotalSeconds / knownDuration.Value.TotalSeconds * 100);
            job.UpdateProgress(pct);

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to persist progress for Job={JobId}", job.Id);
            }
        }
    }
}
