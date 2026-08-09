using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Enums;
using Krautwatch.Domain.Interfaces;

namespace Krautwatch.Application.Downloads;

// ══════════════════════════════════════════════════════════════
// Messages
// ══════════════════════════════════════════════════════════════

/// <summary>Where in the queue to move a job.</summary>
public enum QueueMove
{
    /// <summary>Run it before everything else queued.</summary>
    ToTop = 0,

    /// <summary>Run it after everything else queued.</summary>
    ToBottom = 1,
}

/// <summary>Outcome of a reorder, shaped for display.</summary>
public record ReorderResult(bool Ok, string? Problem = null)
{
    public static ReorderResult Success() => new(true);
    public static ReorderResult NotFound() => new(false, "That download no longer exists.");

    public static ReorderResult NotQueued(DownloadStatus status) =>
        new(false, $"Only a queued download can be reordered; this one is {status}.");
}

// ══════════════════════════════════════════════════════════════
// Command
// ══════════════════════════════════════════════════════════════

/// <summary>
/// Moves a queued download to the top or bottom of the queue (#51).
/// </summary>
/// <remarks>
/// <para>
/// Priority is <b>sparse</b>: moving to the top writes <c>max + 1</c> and to the bottom <c>min - 1</c>,
/// so each move is a single row write with no renumbering of neighbours — see
/// <c>docs/plans/2026-08-09 - download queue.md</c>.
/// </para>
/// <para>
/// Single-step move up/down is deliberately absent. With everything defaulting to <c>0</c> and ties broken
/// by <c>CreatedAt</c>, raising a job above one equal-priority neighbour raises it above all of them, so a
/// correct single step needs renumbering or fractional priorities. Top and bottom solve the case #51
/// actually describes — a manual grab buried behind an RSS-Sync season pack.
/// </para>
/// </remarks>
public class ReorderDownloadHandler(IDownloadJobRepository jobs)
{
    public async Task<ReorderResult> HandleAsync(Guid jobId, QueueMove move, CancellationToken ct = default)
    {
        var job = await jobs.GetByIdAsync(jobId, ct);
        if (job is null)
            return ReorderResult.NotFound();

        // Refused rather than ignored: a running or finished download cannot be reordered, and a control
        // that silently does nothing is the defect this feature exists alongside fixing.
        if (job.Status != DownloadStatus.Queued)
            return ReorderResult.NotQueued(job.Status);

        var queued = await jobs.GetQueuedOrderedAsync(ct);

        // Nothing to move past.
        if (queued.Count <= 1)
            return ReorderResult.Success();

        job.SetPriority(move switch
        {
            QueueMove.ToTop => queued.Max(j => j.Priority) + 1,
            QueueMove.ToBottom => queued.Min(j => j.Priority) - 1,
            _ => job.Priority,
        });

        await jobs.UpdateAsync(job, ct);
        return ReorderResult.Success();
    }
}

// ══════════════════════════════════════════════════════════════
// Mapping — SABnzbd priority ↔ ours
// ══════════════════════════════════════════════════════════════

/// <summary>
/// Translates SABnzbd's priority scale, which Sonarr sends on a grab and expects back in the queue.
/// </summary>
/// <remarks>
/// Honouring the incoming value is what stops an interactive grab being buried behind a season pack an
/// RSS-Sync enqueued moments earlier — Sonarr already marks the former as higher priority.
/// <para/>
/// SABnzbd's <c>-2</c> means <em>paused</em>, which is a queue state rather than a priority. We have no
/// paused state, so it is treated as Low; pretending it paused something would be a worse lie than the
/// hardcoded "Normal" this replaces.
/// </remarks>
public static class SabnzbdPriority
{
    /// <summary>Maps an incoming SABnzbd priority onto <see cref="DownloadJob.Priority"/>.</summary>
    public static int ToJobPriority(string? sabPriority) =>
        int.TryParse(sabPriority, out var value)
            ? Math.Clamp(value, -1, 2)   // -2 (paused) collapses onto Low
            : 0;                          // absent or unparseable — Normal

    /// <summary>Maps a job priority back onto the name SABnzbd clients display.</summary>
    public static string ToDisplayName(int priority) => priority switch
    {
        <= -1 => "Low",
        0 => "Normal",
        1 => "High",
        _ => "Force",
    };
}
