using System.Diagnostics;
using System.Text.RegularExpressions;
using MediathekNext.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace MediathekNext.Infrastructure.Downloads.Ffmpeg;

/// <summary>
/// Downloads a stream URL to disk via ffmpeg.
///
/// Progress is calculated from the `time=` field in ffmpeg's stderr output.
/// When knownDuration is provided, we compute a real 0-100% figure.
/// Without it, we emit elapsed seconds scaled against a 90-minute heuristic,
/// capped at 99% so MarkCompleted() signals the definitive 100%.
/// </summary>
public partial class FfmpegDownloader(ILogger<FfmpegDownloader> logger) : IFfmpegDownloader
{
    [GeneratedRegex(@"time=(\d{2}):(\d{2}):(\d{2})\.(\d{2})", RegexOptions.Compiled)]
    private static partial Regex TimeRegex();

    public async Task<DownloadResult> DownloadAsync(
        Guid jobId,
        string url,
        VideoQuality quality,
        string outputDirectory,
        Func<double, Task> onProgress,
        TimeSpan? knownDuration = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, $"{jobId}_{quality}.mp4");
        var args = $"-hide_banner -loglevel error -stats -y -i \"{url}\" -c copy \"{outputPath}\"";

        logger.LogDebug("ffmpeg {JobId}: {Args}", jobId, args);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName               = "ffmpeg",
                Arguments              = args,
                RedirectStandardError  = true,
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            }
        };

        process.Start();

        await using var reg = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch { /* already exited */ }
        });

        var progressTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(ct) is { } line)
            {
                var pct = ParseProgress(line, knownDuration);
                if (pct.HasValue)
                    await onProgress(pct.Value);
            }
        }, ct);

        await process.WaitForExitAsync(ct);
        await progressTask;

        if (ct.IsCancellationRequested)
            throw new OperationCanceledException(ct);

        if (process.ExitCode != 0)
        {
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException(
                $"ffmpeg exited {process.ExitCode}: {stderr[..Math.Min(500, stderr.Length)]}");
        }

        if (!File.Exists(outputPath))
            throw new FileNotFoundException($"Output file not found: {outputPath}");

        await onProgress(100.0);
        return new DownloadResult(outputPath, new FileInfo(outputPath).Length);
    }

    private static double? ParseProgress(string line, TimeSpan? knownDuration)
    {
        var match = TimeRegex().Match(line);
        if (!match.Success) return null;

        var h  = int.Parse(match.Groups[1].Value);
        var m  = int.Parse(match.Groups[2].Value);
        var s  = int.Parse(match.Groups[3].Value);
        var elapsed = TimeSpan.FromSeconds(h * 3600 + m * 60 + s);

        if (knownDuration.HasValue && knownDuration.Value > TimeSpan.Zero)
        {
            // Real percentage from known episode duration
            var pct = elapsed.TotalSeconds / knownDuration.Value.TotalSeconds * 100.0;
            return Math.Min(Math.Round(pct, 1), 99.0);
        }

        // Heuristic: assume avg 90 min episode, clamp at 99%
        var proxy = elapsed.TotalSeconds / (90 * 60) * 100.0;
        return Math.Min(Math.Round(proxy, 1), 99.0);
    }
}
