using MediathekNext.Domain.Entities;

namespace MediathekNext.Domain.Interfaces;

/// <summary>
/// Abstraction for catalog data sources (MediathekView, ZDF API, ARD API, etc.)
/// See: DR-001
/// </summary>
public interface ICatalogProvider
{
    string ProviderName { get; }
    bool SupportsChannel(string channelId);
    Task<IReadOnlyList<Episode>> FetchCatalogAsync(CancellationToken ct = default);
    Task<Episode?> GetEpisodeDetailAsync(string episodeId, CancellationToken ct = default);
}
