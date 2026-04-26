namespace MediathekNext.Crawlers.Core;

public sealed record CrawlSummary(
    string Source,
    int EpisodesFetched,
    int EpisodesPersisted,
    int Errors,
    TimeSpan Elapsed
);
