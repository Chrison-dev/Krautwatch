using MediathekNext.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
// using TickerQ.Utilities.Interfaces; // TODO: Add proper TickerQ reference

namespace MediathekNext.Infrastructure.Jobs;

/// <summary>
/// Called by TickerQ when a job exhausts all retry attempts.
/// Maps the failed TickerQ function name to the correct DownloadJob terminal state.
///
/// TickerQ passes the original request payload so we can extract the DownloadJobId.
/// TODO: Implement proper TickerQ integration
/// </summary>
public class DownloadJobExceptionHandler(
    AppDbContext db,
    ILogger<DownloadJobExceptionHandler> logger) // : ITickerExceptionHandler
{
    public async Task HandleAsync(
        string functionName,
        Exception exception,
        string? requestPayload,
        CancellationToken ct)
    {
        logger.LogError(exception,
            "TickerQ job exhausted retries: Function={Function}", functionName);

        if (requestPayload is null) return;

        DownloadJobContext? ctx;
        try
        {
            ctx = global::System.Text.Json.JsonSerializer.Deserialize<DownloadJobContext>(requestPayload);
        }
        catch
        {
            logger.LogWarning("Could not deserialise request payload for Function={Function}",
                functionName);
            return;
        }

        if (ctx is null) return;

        var job = await db.DownloadJobs
            .FirstOrDefaultAsync(j => j.Id == ctx.DownloadJobId, ct);

        if (job is null || job.IsTerminal) return;

        var reason = exception.Message;

        switch (functionName)
        {
            case ResolveStreamJob.FunctionName:
                job.MarkUrlUnavailable(reason);
                break;
            case DownloadStreamJob.FunctionName:
                job.MarkDownloadFailed(reason);
                break;
            case FinaliseDownloadJob.FunctionName:
                job.MarkFinaliseFailed(reason);
                break;
            default:
                logger.LogWarning(
                    "Unknown function {Function} — not marking DownloadJob terminal",
                    functionName);
                return;
        }

        await db.SaveChangesAsync(CancellationToken.None);

        logger.LogInformation(
            "DownloadJob {JobId} marked {Status} after retry exhaustion",
            job.Id, job.Status);
    }
}
