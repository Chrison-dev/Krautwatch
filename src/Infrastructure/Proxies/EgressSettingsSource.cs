using Krautwatch.Domain.Interfaces;
using Krautwatch.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Krautwatch.Infrastructure.Proxies;

/// <summary>The egress settings actually in force, and where each came from.</summary>
public record EffectiveEgressSettings(
    string? ProxyUrl,
    bool ProxyListEnabled,
    int MaxCandidates,
    bool ProxyUrlFromConfiguration,
    string? ProxyUrlProblem);

/// <summary>
/// Resolves the egress-proxy settings for geo-restricted downloads (#45), preferring configuration over
/// the database — the same precedence, for the same reason, as <c>TvdbApiKeySource</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Configuration wins deliberately.</b> An operator who sets <c>Download__ProxyUrl</c> in a compose
/// file expects it to apply; silently overriding it with a database row edited months earlier in the UI
/// is a genuinely nasty thing to debug. Where config supplies the proxy, the UI reports it as managed
/// elsewhere and read-only.
/// </para>
/// <para>
/// The database value is read lazily through a scope rather than injected: this is a singleton, and
/// capturing a scoped <c>ISettingsRepository</c> would be a captive dependency. A short TTL rather than
/// explicit invalidation, because the Application layer cannot signal Infrastructure that settings were
/// saved, and re-reading one row a minute is far cheaper than threading an invalidation port through.
/// </para>
/// <para>
/// The proxy URL goes through <see cref="ISecretResolver"/>: it can embed credentials
/// (<c>http://user:pass@host</c>), so an operator must be able to store <c>env:DE_PROXY</c> rather than
/// the secret itself.
/// </para>
/// </remarks>
public sealed class EgressSettingsSource(
    IServiceScopeFactory scopeFactory,
    ISecretResolver secrets,
    EgressProxyOptions configured,
    ILogger<EgressSettingsSource> logger)
{
    private static readonly TimeSpan DatabaseTtl = TimeSpan.FromSeconds(60);

    private readonly Lock _gate = new();
    private EffectiveEgressSettings? _cached;
    private DateTimeOffset _readAt = DateTimeOffset.MinValue;

    /// <summary>True when configuration supplies the proxy URL, so the UI must not offer to edit it.</summary>
    public bool ProxyUrlFromConfiguration => !string.IsNullOrWhiteSpace(configured.ProxyUrl);

    public EffectiveEgressSettings Current
    {
        get
        {
            lock (_gate)
            {
                if (_cached is not null && DateTimeOffset.UtcNow - _readAt < DatabaseTtl)
                    return _cached;

                // Stamp regardless of outcome: a database that is down must not turn every download into
                // a retry storm on the settings row.
                _readAt = DateTimeOffset.UtcNow;
                _cached = Resolve();
                return _cached;
            }
        }
    }

    /// <summary>Drops the cache so the next read reflects a settings save immediately.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _cached = null;
            _readAt = DateTimeOffset.MinValue;
        }
    }

    private EffectiveEgressSettings Resolve()
    {
        string? storedProxyUrl = null;
        var listEnabled = configured.ProxyList.Enabled;
        var maxCandidates = configured.ProxyList.MaxCandidates;

        try
        {
            using var scope = scopeFactory.CreateScope();
            var settings = scope.ServiceProvider.GetService<ISettingsRepository>();
            var stored = settings?.GetAsync().GetAwaiter().GetResult();

            if (stored is not null)
            {
                storedProxyUrl = stored.EgressProxyUrl;

                // Config opts Mode B on; the database can too. Either being true is enough — an operator
                // who enabled it in compose should not have it silently switched off by a stale row.
                listEnabled |= stored.EgressProxyListEnabled;

                if (stored.EgressProxyListMaxCandidates > 0)
                    maxCandidates = stored.EgressProxyListMaxCandidates;
            }
        }
        catch (Exception ex)
        {
            // Unreachable settings must not disable egress entirely — fall back to configuration, which
            // is what this deployment had before the settings row existed.
            logger.LogWarning(ex, "Could not read stored egress settings; using configuration only.");
        }

        // Configuration first, exactly as documented above.
        var raw = ProxyUrlFromConfiguration ? configured.ProxyUrl : storedProxyUrl;
        var resolved = secrets.Resolve(raw);

        return new EffectiveEgressSettings(
            ProxyUrl: resolved.HasValue ? resolved.Value : null,
            ProxyListEnabled: listEnabled,
            MaxCandidates: maxCandidates,
            ProxyUrlFromConfiguration: ProxyUrlFromConfiguration,
            ProxyUrlProblem: resolved.Origin == SecretOrigin.Unresolved ? resolved.Problem : null);
    }
}
