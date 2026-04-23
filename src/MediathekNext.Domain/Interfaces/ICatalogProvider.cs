using MediathekNext.Domain.Entities;

namespace MediathekNext.Domain.Interfaces;

// ──────────────────────────────────────────────────────────────
// Progress reporting
// ──────────────────────────────────────────────────────────────

public enum CatalogFetchPhase { Downloading, Parsing }

public record CatalogFetchProgress(
    CatalogFetchPhase Phase,
    int PercentComplete = 0,
    long EntriesParsed = 0,
    long? TotalEntries = null);

// ──────────────────────────────────────────────────────────────
// Provider interface
// ──────────────────────────────────────────────────────────────

/// <summary>
/// Abstraction for catalog data sources (MediathekView, ZDF API, ARD API, etc.)
/// See: DR-001
/// </summary>
public interface ICatalogProvider
{
    string ProviderName { get; }
    bool SupportsChannel(string channelId);

    Task<IReadOnlyList<Episode>> FetchCatalogAsync(
        IProgress<CatalogFetchProgress>? progress = null,
        CancellationToken ct = default);

    Task<Episode?> GetEpisodeDetailAsync(string episodeId, CancellationToken ct = default);
}
