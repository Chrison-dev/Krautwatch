using System.Diagnostics;
using System.Text;
using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Interfaces;
using Krautwatch.Infrastructure.Jobs;
using Microsoft.Extensions.Logging;

namespace Krautwatch.Infrastructure.Downloads;

/// <summary>
/// Downloads an <b>HLS</b> (m3u8) stream by remuxing it into an MP4 container with ffmpeg — no
/// re-encode (<c>-c copy</c>), so it's fast and lossless (DR-010's deferred orchestration step, now
/// wired). Reports progress from ffmpeg's <c>-progress</c> output against the episode's duration.
/// Requires <c>ffmpeg</c> on PATH (the Downloader image bundles it); override with
/// <c>KRAUTWATCH_FFMPEG</c>.
/// </summary>
public sealed class FfmpegDownloadProvider(FileNamingService naming, ILogger<FfmpegDownloadProvider> logger)
    : IDownloadProvider
{
    private const string UserAgent = "Krautwatch/1.0 (+https://github.com/Chrison-dev/Krautwatch)";
    private static readonly string FfmpegPath = Environment.GetEnvironmentVariable("KRAUTWATCH_FFMPEG") ?? "ffmpeg";

    public async Task<DownloadResult> DownloadAsync(
        DownloadJob job, string outputDirectory, IProgress<double> progress, CancellationToken ct = default)
    {
        var episode = job.Episode
            ?? throw new InvalidOperationException($"Job {job.Id} has no episode metadata to name the file.");

        var finalPath = naming.BuildFinalPath(outputDirectory, episode, job.Quality);
        var tempPath = Path.ChangeExtension(naming.BuildTempPath(outputDirectory, job.Id, job.Quality), ".mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        var startInfo = new ProcessStartInfo
        {
            FileName = FfmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[]
        {
            "-hide_banner", "-nostdin", "-y",
            "-user_agent", UserAgent,
            "-i", job.StreamUrl,
            "-c", "copy",                 // remux only — no transcode
            "-bsf:a", "aac_adtstoasc",    // TS/ADTS AAC → MP4-safe AAC
            "-progress", "pipe:1", "-nostats",
            tempPath,
        })
            startInfo.ArgumentList.Add(arg);

        logger.LogInformation("Remuxing (ffmpeg) {Episode} → {Path}", episode.Title, finalPath);

        using var process = new Process { StartInfo = startInfo };
        var stderrTail = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stderrTail.AppendLine(e.Data);
            if (stderrTail.Length > 8192) stderrTail.Remove(0, stderrTail.Length - 8192);
        };

        try { process.Start(); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not start ffmpeg ('{FfmpegPath}'). Is it installed / on PATH?", ex);
        }

        // Cancellation must also kill ffmpeg — the token alone doesn't reach the child process.
        await using var kill = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* already gone */ }
        });

        process.BeginErrorReadLine();

        try
        {
            var totalSeconds = episode.Duration.TotalSeconds;
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(ct)) is not null)
            {
                if (totalSeconds > 0
                    && line.StartsWith("out_time_us=", StringComparison.Ordinal)
                    && long.TryParse(line.AsSpan("out_time_us=".Length), out var us) && us > 0)
                    progress.Report(Math.Clamp(us / 1_000_000.0 / totalSeconds * 100, 0, 100));
            }

            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempPath); // ffmpeg was killed (cancel/shutdown) — drop the partial remux
            throw;
        }

        if (process.ExitCode != 0)
        {
            TryDelete(tempPath);
            throw new InvalidOperationException($"ffmpeg exited {process.ExitCode}: {Tail(stderrTail.ToString())}");
        }

        File.Move(tempPath, finalPath, overwrite: true);
        var size = new FileInfo(finalPath).Length;
        logger.LogInformation("Remuxed {Episode} ({Size} bytes)", episode.Title, size);
        return new DownloadResult(finalPath, size);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private static string Tail(string text, int max = 500) =>
        text.Length <= max ? text.Trim() : text[^max..].Trim();
}
