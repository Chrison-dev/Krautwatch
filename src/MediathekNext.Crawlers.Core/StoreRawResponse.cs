namespace MediathekNext.Crawlers.Core;

public record StoreRawResponseCommand(string Source, string ItemId, string Json);

public sealed class StoreRawResponseHandler(IRawResponseStore store)
{
    public Task HandleAsync(StoreRawResponseCommand cmd, CancellationToken ct = default)
        => store.StoreAsync(cmd.Source, cmd.ItemId, cmd.Json, ct);
}
