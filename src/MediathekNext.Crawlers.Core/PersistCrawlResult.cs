namespace MediathekNext.Crawlers.Core;

public record PersistCrawlResultCommand(CrawlResult Result);

public sealed class PersistCrawlResultHandler(ICrawlRepository repository)
{
    public Task HandleAsync(PersistCrawlResultCommand cmd, CancellationToken ct = default)
        => repository.UpsertAsync(cmd.Result, ct);
}
