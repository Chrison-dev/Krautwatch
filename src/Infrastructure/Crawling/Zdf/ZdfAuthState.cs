using System.Net;

namespace Krautwatch.Infrastructure.Crawling.Zdf;

/// <summary>
/// Thrown when api.zdf.de rejects our <c>Api-Auth</c> bearer (#13).
/// </summary>
/// <remarks>
/// A distinct type because a rejected key is a distinct condition: every other crawl failure is
/// transient and worth retrying, and this one will fail identically until a human supplies a new key.
/// Callers that treat "no episodes" as normal — which is most of them — would otherwise turn a broken
/// indexer into a quiet one.
/// </remarks>
public sealed class ZdfAuthRejectedException(HttpStatusCode statusCode)
    : Exception($"ZDF rejected the Api-Auth key ({(int)statusCode} {statusCode}) — it has most likely " +
                $"been rotated. Set {ZdfOptions.SectionName}:{nameof(ZdfOptions.ApiAuthKey)} to the " +
                "current value.")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

/// <summary>
/// Tracks whether ZDF is currently accepting our key, so the condition can be reported somewhere a
/// human will see it rather than only in a log line that scrolls past.
/// </summary>
/// <remarks>
/// A singleton per host: the client writes it, <see cref="ZdfAuthHealthCheck"/> reads it. Deliberately
/// in-memory and per-process — it describes this process's live experience of the API, and outlives
/// nothing. State that needs to survive a restart would have to be the key itself, which is
/// configuration.
/// </remarks>
public sealed class ZdfAuthState
{
    private readonly Lock _gate = new();

    private int _consecutiveRejections;
    private DateTimeOffset? _firstRejectionAt;
    private HttpStatusCode? _lastStatusCode;

    /// <summary>A request was answered — the key is good.</summary>
    public void RecordSuccess()
    {
        lock (_gate)
        {
            _consecutiveRejections = 0;
            _firstRejectionAt = null;
            _lastStatusCode = null;
        }
    }

    /// <summary>A request came back 401/403.</summary>
    public void RecordRejection(HttpStatusCode statusCode, DateTimeOffset now)
    {
        lock (_gate)
        {
            _consecutiveRejections++;
            _firstRejectionAt ??= now;
            _lastStatusCode = statusCode;
        }
    }

    /// <summary>A snapshot of the current state, for reporting.</summary>
    public ZdfAuthSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new ZdfAuthSnapshot(_consecutiveRejections, _firstRejectionAt, _lastStatusCode);
        }
    }
}

/// <param name="ConsecutiveRejections">Rejections since the last success. Zero means healthy.</param>
/// <param name="FirstRejectionAt">When the current run of rejections began.</param>
/// <param name="LastStatusCode">The status the API last rejected us with.</param>
public readonly record struct ZdfAuthSnapshot(
    int ConsecutiveRejections,
    DateTimeOffset? FirstRejectionAt,
    HttpStatusCode? LastStatusCode)
{
    public bool IsRejected => ConsecutiveRejections > 0;
}
