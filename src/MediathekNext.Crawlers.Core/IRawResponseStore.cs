namespace MediathekNext.Crawlers.Core;

/// <summary>
/// Stores raw API responses as they are fetched, enabling replay and debugging.
/// </summary>
public interface IRawResponseStore
{
    Task StoreAsync(string source, string itemId, string json, CancellationToken ct = default);
}
