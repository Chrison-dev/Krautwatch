namespace Krautwatch.Domain.Enums;

/// <summary>
/// What a Newznab search should do when the requested show has not been crawled yet and has to be resolved
/// against the broadcasters (#58 / DR-011). A genuine trade-off, so it is the operator's choice.
/// </summary>
public enum SearchWaitMode
{
    /// <summary>
    /// Answer quickly with whatever has been resolved so far, letting the crawl finish in the background —
    /// the first search may under-report, the next one is complete and instant. The safe default: Sonarr
    /// treats a slow indexer as a broken one.
    /// </summary>
    ReturnFast = 0,

    /// <summary>
    /// Wait for the resolution to finish so the very first search is complete. Still bounded by
    /// <c>CrawlTimeout</c> — an unbounded wait would hang the request forever on a stuck crawl.
    /// <para>
    /// Be aware this puts a full multi-hop broadcaster crawl inside Sonarr's HTTP request. If it exceeds
    /// Sonarr's own indexer timeout, Sonarr gives up and may mark the indexer as failing — so a generous
    /// wait here can look like an outage from the other side.
    /// </para>
    /// </summary>
    WaitForComplete = 1,
}
