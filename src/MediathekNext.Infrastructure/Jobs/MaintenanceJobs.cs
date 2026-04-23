using MediathekNext.Domain.Enums;
using MediathekNext.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TickerQ.Utilities.Base;

namespace MediathekNext.Infrastructure.Jobs;

/// <summary>
/// Nightly maintenance jobs run via TickerQ cron.
/// Both jobs are seeded in TickerQSeedService.
/// </summary>
public class MaintenanceJobs(
    AppDbContext db,
    ILogger<MaintenanceJobs> logger)
{
    public const string CleanupOldJobsFunctionName         = "CleanupOldJobs";
    public const string CleanupOrphanedTempFilesFunctionName = "CleanupOrphanedTempFiles";

    // ── Job 1: Delete old terminal DownloadJob rows ───────────────────────

    /// <summary>
    /// Deletes Completed and Cancelled DownloadJob rows older than 30 days.
    /// Failed rows are retained for 7 days so users can see what went wrong.
    /// </summary>
    [TickerFunction(CleanupOldJobsFunctionName)]
    public async Task CleanupOldJobsAsync(TickerFunctionContext context, CancellationToken ct)
    {
        var completedCutoff = DateTimeOffset.UtcNow.AddDays(-30);
        var failedCutoff    = DateTimeOffset.UtcNow.AddDays(-7);

        var deleted = await db.DownloadJobs
            .Where(j =>
                (j.Status == DownloadStatus.Completed || j.Status == DownloadStatus.Cancelled)
                    && j.CompletedAt < completedCutoff
                ||
                (j.Status == DownloadStatus.UrlUnavailable ||
                 j.Status == DownloadStatus.DownloadFailed ||
                 j.Status == DownloadStatus.FinaliseFailed)
                    && j.CompletedAt < failedCutoff)
            .ExecuteDeleteAsync(ct);

        logger.LogInformation("CleanupOldJobs: deleted {Count} rows", deleted);
    }

    // ── Job 2: Delete orphaned temp files ────────────────────────────────

    /// <summary>
    /// Scans /downloads/.tmp for .part files that have no corresponding active DownloadJob.
    /// Removes them to prevent unbounded disk growth from failed downloads.
    /// </summary>
    [TickerFunction(CleanupOrphanedTempFilesFunctionName)]
    public async Task CleanupOrphanedTempFilesAsync(
        TickerFunctionContext context,
        CancellationToken ct)
    {
        // Find all active job IDs that might still need their temp files
        var activeJobIds = await db.DownloadJobs
            .Where(j => j.Status == DownloadStatus.Downloading ||
                        j.Status == DownloadStatus.Finalising)
            .Select(j => j.Id)
            .ToListAsync(ct);

        // Build the set of temp paths that are still in use
        var activeTempPaths = await db.DownloadJobs
            .Where(j => activeJobIds.Contains(j.Id) && j.TempPath != null)
            .Select(j => j.TempPath!)
            .ToListAsync(ct);

        // Find all .part files on disk — look relative to known download job temp paths
        // We only have the temp paths from the DB, not the base directory directly,
        // so we infer the .tmp dir from any known path. Fallback: skip if none.
        var tmpDirs = activeTempPaths
            .Select(Path.GetDirectoryName)
            .Where(d => d != null && Directory.Exists(d))
            .Distinct()
            .ToList();

        // Also check common temp dir from any completed/failed jobs
        var knownTempDirs = await db.DownloadJobs
            .Where(j => j.TempPath != null)
            .Select(j => j.TempPath!)
            .ToListAsync(ct);

        var allTmpDirs = knownTempDirs
            .Select(Path.GetDirectoryName)
            .Where(d => d != null && Directory.Exists(d))
            .Concat(tmpDirs)
            .Distinct()
            .ToList();

        var deleted = 0;
        foreach (var tmpDir in allTmpDirs)
        {
            foreach (var file in Directory.GetFiles(tmpDir!, "*.part"))
            {
                if (activeTempPaths.Contains(file)) continue;

                // Only delete files older than 1 hour — give in-progress downloads buffer
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(file);
                if (age < TimeSpan.FromHours(1)) continue;

                try
                {
                    File.Delete(file);
                    deleted++;
                    logger.LogInformation("Deleted orphaned temp file: {Path}", file);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not delete orphaned temp file: {Path}", file);
                }
            }
        }

        logger.LogInformation("CleanupOrphanedTempFiles: deleted {Count} files", deleted);
    }
}
