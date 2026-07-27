using Krautwatch.Domain.Enums;

namespace Krautwatch.Domain.Entities;

/// <summary>
/// A configured Sonarr or Radarr instance that Krautwatch calls **outbound** — to test connectivity and
/// (per #6) to fetch the monitored-series list that becomes the crawl work-list.
/// </summary>
/// <remarks>
/// This is the opposite direction to <c>Krautwatch:ApiKey</c>, which is the key `*arr` apps use to call
/// <i>us</i>. Here <see cref="ApiKey"/> is <i>their</i> key, held so we can authenticate to them.
/// <para>
/// The last-test fields are a cache of the most recent connectivity check so the UI can show state
/// without re-probing every instance on each page load.
/// </para>
/// </remarks>
public class ArrInstance
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Operator-facing label, so several instances of the same kind stay distinguishable.</summary>
    public string Name { get; set; } = string.Empty;

    public ArrKind Kind { get; set; }

    /// <summary>
    /// Absolute base URL, e.g. <c>http://sonarr:8989</c>. May include a path when the instance sits
    /// behind a reverse proxy on a subpath.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// The instance's API key. A credential: never returned by read models — query DTOs carry a masked
    /// form only, so the UI cannot be used to harvest keys.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Lets an instance be kept but skipped, rather than deleted and re-entered.</summary>
    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // ── Cached outcome of the last connectivity test ──────────
    public DateTimeOffset? LastTestedAt { get; set; }
    public bool? LastTestOk { get; set; }

    /// <summary>App version on success, or an actionable reason on failure.</summary>
    public string? LastTestMessage { get; set; }
}
