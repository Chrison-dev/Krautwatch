using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Krautwatch.Application.Crawling;

// ============================================================
// Action (IO-driven, DR-009)
// ============================================================

/// <summary>
/// Turns the shows an operator monitors in Sonarr/Radarr into crawl targets, so the RSS feed carries
/// what they actually watch without them hand-curating <c>Crawl:Targets</c> (#6).
/// </summary>
/// <remarks>
/// <para>
/// A <b>pre-warm</b>, never a work-list. DR-011 settled that search resolves on demand, so nothing here
/// may become a dependency: this is opt-in, purely additive, and every failure degrades to the
/// configured list. An <c>*arr</c> instance being down must cost a log line and nothing else.
/// </para>
/// <para>
/// The mapping problem the issue called "the hard part" is mostly already solved elsewhere:
/// <see cref="ShowMapping"/> maps a TVDB id to one of our shows, and our show ids carry the provider
/// (<c>{providerKey}:{slug}</c>). A monitored series with a mapping therefore yields one precise
/// target; an unmapped one falls back to its title, tried against each broadcaster this host serves.
/// A miss there costs a single search, and it is self-correcting — once a grab creates a mapping, the
/// fan-out collapses to the mapped target.
/// </para>
/// </remarks>
public class PreWarmCrawlTargetsHandler(
    IArrInstanceRepository instances,
    IArrClient arr,
    IShowMappingRepository mappings,
    ILogger<PreWarmCrawlTargetsHandler> logger)
{
    /// <param name="providerKeys">
    /// The broadcasters this host can actually crawl. Each agent handles its own dispatched commands,
    /// so a target for a provider it has no crawler for is dropped on arrival — filtering here keeps
    /// that from looking like a mystery.
    /// </param>
    /// <param name="maxTargets">Upper bound on what is returned; see <see cref="CrawlOptions.PreWarmMaxTargets"/>.</param>
    public async Task<IReadOnlyList<CrawlTarget>> HandleAsync(
        IReadOnlyCollection<string> providerKeys,
        int maxTargets,
        CancellationToken ct = default)
    {
        if (providerKeys.Count == 0) return [];

        var enabled = await instances.GetEnabledAsync(ct);
        if (enabled.Count == 0)
        {
            logger.LogDebug("Pre-warm is on, but no *arr instance is configured and enabled.");
            return [];
        }

        // Mapped targets first, so that if the cap bites it drops guesses rather than known-good shows.
        var mapped = new List<CrawlTarget>();
        var byTitle = new List<CrawlTarget>();

        foreach (var instance in enabled)
        {
            foreach (var item in await arr.GetMonitoredAsync(instance, ct))
            {
                var known = item.TvdbId is { } tvdbId
                    ? await mappings.GetByTvdbIdAsync(tvdbId, ct)
                    : [];

                if (known.Count > 0)
                {
                    // Mapped, so we know which broadcaster carries it. If that is not one this host
                    // serves, the answer is to schedule nothing — the agent that does serve it will.
                    // Falling back to a title guess here would search ARD for a show we already know
                    // is on ZDF.
                    if (TargetFor(known, providerKeys) is { } target)
                        mapped.Add(target);

                    continue;
                }

                byTitle.AddRange(providerKeys.Select(provider => new CrawlTarget(provider, item.Title)));
            }
        }

        var targets = mapped
            .Concat(byTitle)
            .Distinct()
            .ToList();

        if (targets.Count > maxTargets)
        {
            // Said out loud rather than truncated quietly: someone monitoring hundreds of series would
            // otherwise wonder why only some of them ever appear in the feed.
            logger.LogWarning(
                "Pre-warm produced {Produced} target(s); keeping the first {Kept} (Crawl:PreWarmMaxTargets). " +
                "Mapped shows are kept ahead of title guesses.",
                targets.Count, maxTargets);

            targets = targets.Take(maxTargets).ToList();
        }

        logger.LogInformation("Pre-warmed {Count} crawl target(s) from {Instances} *arr instance(s).",
            targets.Count, enabled.Count);

        return targets;
    }

    /// <summary>
    /// The target these mappings resolve to on one of <paramref name="providerKeys"/>, or null when
    /// they all point at broadcasters this host does not serve.
    /// </summary>
    private static CrawlTarget? TargetFor(
        IReadOnlyList<ShowMapping> known,
        IReadOnlyCollection<string> providerKeys)
    {
        // Best-trusted first, so an operator-confirmed mapping wins over an automatic one.
        foreach (var mapping in known)
        {
            var separator = mapping.ShowId.IndexOf(':');
            if (separator <= 0) continue;

            var provider = mapping.ShowId[..separator];
            if (!providerKeys.Contains(provider, StringComparer.OrdinalIgnoreCase)) continue;

            // The show's own title is the query the crawler searches with; the id's slug is lossy.
            var title = mapping.Show?.Title;
            if (string.IsNullOrWhiteSpace(title)) continue;

            return new CrawlTarget(provider, title);
        }

        return null;
    }
}
