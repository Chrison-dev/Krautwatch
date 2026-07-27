using Krautwatch.Domain.Entities;

namespace Krautwatch.Domain.Interfaces;

/// <summary>Persistence for configured Sonarr/Radarr instances.</summary>
public interface IArrInstanceRepository
{
    Task<IReadOnlyList<ArrInstance>> GetAllAsync(CancellationToken ct = default);
    Task<ArrInstance?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Enabled instances only — the work-list for outbound calls such as #6's monitored-series poll.</summary>
    Task<IReadOnlyList<ArrInstance>> GetEnabledAsync(CancellationToken ct = default);

    Task AddAsync(ArrInstance instance, CancellationToken ct = default);
    Task UpdateAsync(ArrInstance instance, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <summary>
/// Outbound HTTP boundary to a Sonarr/Radarr instance. Starts with connectivity checking; #6 extends the
/// same port with the monitored-series fetch, which is why it is a port rather than a UI helper.
/// </summary>
public interface IArrClient
{
    Task<ArrConnectionResult> TestConnectionAsync(ArrInstance instance, CancellationToken ct = default);
}

/// <summary>
/// Outcome of a connectivity test. Failure modes are distinguished deliberately: "it doesn't work" is
/// the single most common self-hosting complaint, and the operator can only act on a specific cause.
/// </summary>
public record ArrConnectionResult(bool Ok, ArrConnectionFailure Failure, string Message)
{
    public static ArrConnectionResult Success(string appName, string version) =>
        new(true, ArrConnectionFailure.None, $"{appName} {version}");

    public static ArrConnectionResult Fail(ArrConnectionFailure failure, string message) =>
        new(false, failure, message);
}

public enum ArrConnectionFailure
{
    None = 0,

    /// <summary>DNS/TCP/timeout — wrong host or port, or the instance is down.</summary>
    Unreachable = 1,

    /// <summary>TLS handshake or certificate rejection — common with self-signed certs.</summary>
    TlsFailure = 2,

    /// <summary>401/403 — reached it, but the API key is wrong.</summary>
    Unauthorized = 3,

    /// <summary>404 — reached a server but not the API; usually a reverse-proxy subpath left off the base URL.</summary>
    ApiNotFound = 4,

    /// <summary>200, but the response is not an `*arr` system-status payload — usually the wrong port entirely.</summary>
    NotAnArrInstance = 5,

    Unexpected = 99,
}
