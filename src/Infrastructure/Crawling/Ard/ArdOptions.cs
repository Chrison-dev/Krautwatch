namespace Krautwatch.Infrastructure.Crawling.Ard;

/// <summary>
/// ARD-platform crawl limits, bound from the <c>Ard</c> configuration section (#9).
/// </summary>
public sealed class ArdOptions
{
    public const string SectionName = "Ard";

    /// <summary>Items per page when walking a widget. 100 is what ARD's own player asks for.</summary>
    public int PageSize { get; set; } = 100;

    /// <summary>
    /// Most episodes to collect for one show, across all its pages.
    /// </summary>
    /// <remarks>
    /// A real ceiling, not a formality: ARD widgets go to four figures — tagesschau's
    /// "Bundestag und Parlamente" reports 1588 items — and the crawler fetches the item page of
    /// <i>every</i> episode it lists, on every cycle. Uncapped, one show would mean well over a
    /// thousand requests to ARD every six hours. Listings are newest-first, so a cap keeps the part
    /// that matters to an indexer and drops the archive. Raise it if you want deeper history and can
    /// live with the crawl length; the crawler logs whenever the cap truncates a show.
    /// </remarks>
    public int MaxEpisodesPerShow { get; set; } = 200;
}
