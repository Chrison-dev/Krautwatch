using Krautwatch.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Krautwatch.Infrastructure.Tvdb;

/// <summary>Where the effective TVDB API key came from — surfaced in the settings UI.</summary>
public enum TvdbKeyOrigin
{
    /// <summary>No key anywhere; TVDB matching is inert.</summary>
    None = 0,

    /// <summary>Supplied by configuration — environment variable, user-secrets or appsettings.</summary>
    Configuration = 1,

    /// <summary>Entered by the operator in the settings UI and stored in the database.</summary>
    Database = 2,
}

/// <summary>
/// Resolves the TVDB API key, preferring configuration over the database.
/// </summary>
/// <remarks>
/// <para>
/// <b>Configuration wins deliberately.</b> An operator who sets <c>TvdbConfiguration__ApiKey</c> in a
/// compose file expects it to apply; silently overriding it with a stale database row left over from an
/// earlier UI edit is a genuinely nasty thing to debug. So config is authoritative and the UI reports the
/// key as managed elsewhere when it is present.
/// </para>
/// <para>
/// The database value is read lazily through a scope rather than injected: this is a singleton (the token
/// provider is), and capturing a scoped <c>ISettingsRepository</c> would be a captive dependency.
/// </para>
/// </remarks>
public class TvdbApiKeySource(
    IServiceScopeFactory scopeFactory,
    ISecretResolver secrets,
    ILogger<TvdbApiKeySource> logger,
    string? configuredKey,
    string? configuredPin)
{
    /// <summary>
    /// How long a database read is trusted. A short TTL rather than explicit invalidation: the Application
    /// layer cannot reach into Infrastructure to signal a settings save, and re-reading a single row once a
    /// minute is far cheaper than threading an invalidation port through the layers to save it.
    /// </summary>
    private static readonly TimeSpan DatabaseKeyTtl = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private string? _databaseKey;
    private DateTimeOffset _databaseReadAt = DateTimeOffset.MinValue;

    public string? Pin => configuredPin;

    /// <summary>The key to authenticate with, or null when TVDB is unconfigured.</summary>
    public string? Current
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(configuredKey))
                return configuredKey.Trim();

            EnsureDatabaseLoaded();
            lock (_gate)
                return string.IsNullOrWhiteSpace(_databaseKey) ? null : _databaseKey.Trim();
        }
    }

    public TvdbKeyOrigin Origin =>
        !string.IsNullOrWhiteSpace(configuredKey) ? TvdbKeyOrigin.Configuration
        : Current is not null ? TvdbKeyOrigin.Database
        : TvdbKeyOrigin.None;

    public bool IsConfigured => Current is not null;

    /// <summary>Drops the cached database read so the next access re-reads it immediately.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _databaseKey = null;
            _databaseReadAt = DateTimeOffset.MinValue;
        }
    }

    private void EnsureDatabaseLoaded()
    {
        lock (_gate)
        {
            if (DateTimeOffset.UtcNow - _databaseReadAt < DatabaseKeyTtl)
                return;

            // Stamp the read time regardless of outcome: a database that is down must not turn every key
            // read into a retry storm on the request path.
            _databaseReadAt = DateTimeOffset.UtcNow;

            try
            {
                using var scope = scopeFactory.CreateScope();
                var settings = scope.ServiceProvider.GetService<ISettingsRepository>();
                var stored = settings?.GetAsync().GetAwaiter().GetResult()?.TvdbApiKey;

                // The stored key may be a pointer (`env:`/`file:`) rather than the secret itself. An
                // unresolvable one leaves TVDB unconfigured, which degrades matching but never breaks
                // search — the same outcome as no key at all, and already warned about by the resolver.
                var resolved = secrets.Resolve(stored);
                _databaseKey = resolved.HasValue ? resolved.Value : null;
            }
            catch (Exception ex)
            {
                // Unconfigured TVDB degrades matching; it must never break search.
                logger.LogWarning(ex, "Could not read the stored TVDB API key; treating TVDB as unconfigured");
                _databaseKey = null;
            }
        }
    }
}
