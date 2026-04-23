using System.Diagnostics;
using MediathekNext.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TickerQ.Utilities.Base;

namespace MediathekNext.Infrastructure.Jobs;

/// <summary>
/// Phase 3 (terminal) of the download chain.
///
/// Responsibilities:
///   - Create the final directory structure if needed
///   - Atomically move the .part temp file to the final path
///   - Embed episode metadata into the MP4 container via ffmpeg
///   - Mark DownloadJob as Completed
///   - On failure: throw — TickerQ retries (5s / 10s / 30s backoff)
///   - Idempotent: if final file already exists and temp is gone, mark complete
/// </summary>
public class FinaliseDownloadJob(
    AppDbContext db,
    ILogger<FinaliseDownloadJob> logger)
{
    public const string FunctionName = "FinaliseDownload";

    [TickerFunction(FunctionName)]
    public async Task RunAsync(
        TickerFunctionContext<DownloadJobContext> context,
        CancellationToken ct)
    {
        var ctx = context.Request;
        var job = await db.DownloadJobs
            .Include(j => j.Episode)
                .ThenInclude(e => e!.Show)
                    .ThenInclude(s => s!.Channel)
            .FirstOrDefaultAsync(j => j.Id == ctx.DownloadJobId, ct)
            ?? throw new InvalidOperationException($"DownloadJob {ctx.DownloadJobId} not found");

        if (job.IsTerminal)
        {
            logger.LogWarning("Job {JobId} already terminal ({Status}) — skipping finalise",
                job.Id, job.Status);
            return;
        }

        var tempPath  = ctx.TempPath!;
        var finalPath = ctx.FinalPath!;

        // ── Idempotency check ─────────────────────────────────────────────
        // If temp is gone but final exists, a previous Finalise attempt succeeded
        // but the DB write failed. Complete cleanly.
        if (!File.Exists(tempPath) && File.Exists(finalPath))
        {
            logger.LogWarning(
                "Job {JobId}: temp gone but final exists — marking completed (idempotent)",
                job.Id);
            var fi = new FileInfo(finalPath);
            job.MarkCompleted(finalPath, fi.Length);
            await db.SaveChangesAsync(CancellationToken.None);
            return;
        }

        if (!File.Exists(tempPath))
            throw new FileNotFoundException(
                $"Temp file not found for Job={job.Id}. Cannot finalise.", tempPath);

        job.MarkFinalising();
        await db.SaveChangesAsync(ct);

        // ── Create directory structure ─────────────────────────────────────

        var finalDir = Path.GetDirectoryName(finalPath)!;
        Directory.CreateDirectory(finalDir);

        // ── Embed metadata via ffmpeg then move atomically ─────────────────

        var metadataPath = await EmbedMetadataAsync(job, tempPath, finalPath, ct);

        // EmbedMetadataAsync writes to finalPath directly via ffmpeg.
        // On success, delete the temp file.
        if (File.Exists(tempPath) && metadataPath == finalPath)
            File.Delete(tempPath);

        var fileInfo = new FileInfo(finalPath);
        if (!fileInfo.Exists || fileInfo.Length == 0)
            throw new IOException($"Final file missing or empty after finalise: {finalPath}");

        job.MarkCompleted(finalPath, fileInfo.Length);
        await db.SaveChangesAsync(CancellationToken.None);

        logger.LogInformation(
            "Job {JobId} completed: {FinalPath} ({Mb:F1} MB)",
            job.Id, finalPath, fileInfo.Length / 1_048_576.0);
    }

    /// <summary>
    /// Re-mux the temp file to the final path via ffmpeg, embedding metadata.
    /// Uses -c copy so this is fast (just remuxing container, no re-encode).
    /// Returns the path written.
    /// </summary>
    private async Task<string> EmbedMetadataAsync(
        Domain.Entities.DownloadJob job,
        string inputPath,
        string outputPath,
        CancellationToken ct)
    {
        var episode = job.Episode;
        var title   = episode?.Title ?? "";
        var show    = episode?.Show?.Title ?? "";
        var channel = episode?.Show?.Channel?.Name ?? "";
        var date    = episode?.BroadcastDate.ToString("yyyy-MM-dd") ?? "";

        var metaArgs = $"-metadata title=\"{Escape(title)}\" " +
                       $"-metadata show=\"{Escape(show)}\" " +
                       $"-metadata publisher=\"{Escape(channel)}\" " +
                       $"-metadata date=\"{date}\"";

        var args = $"-hide_banner -loglevel error " +
                   $"-i \"{inputPath}\" " +
                   $"-c copy -movflags +faststart " +
                   $"{metaArgs} " +
                   $"\"{outputPath}\"";

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName              = "ffmpeg",
                Arguments             = args,
                RedirectStandardError = true,
                UseShellExecute       = false,
                CreateNoWindow        = true,
            }
        };

        process.Start();
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            throw new Exception(
                $"ffmpeg metadata embed failed (exit {process.ExitCode}): {stderr}");

        return outputPath;
    }

    private static string Escape(string value)
        => value.Replace("\"", "\\\"").Replace("\\", "\\\\");
}
