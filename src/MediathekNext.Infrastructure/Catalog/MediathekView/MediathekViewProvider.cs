using MediathekNext.Domain.Entities;
using MediathekNext.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediathekNext.Infrastructure.Catalog.MediathekView;

/// <summary>
/// ICatalogProvider backed by the MediathekView community filmliste.
/// Downloads the XZ-compressed filmliste, parses it incrementally,
/// yields domain Episode entities for ARD and ZDF.
///
/// Reports download and parse progress via IProgress<CatalogFetchProgress>.
/// Supports MaxEntries cap for dev/debug mode (0 = unlimited).
/// </summary>
public class MediathekViewProvider(
    HttpClient httpClient,
    FilmlisteParser parser,
    IOptions<MediathekViewOptions> options,
    ILogger<MediathekViewProvider> logger) : ICatalogProvider
{
    public string ProviderName => "mediathekview";
    public bool SupportsChannel(string channelId) => channelId is "ard" or "zdf";

    public async Task<IReadOnlyList<Episode>> FetchCatalogAsync(
        IProgress<CatalogFetchProgress>? progress = null,
        CancellationToken ct = default)
    {
        var opts = options.Value;
        var url  = opts.FilmlisteUrl;
        var maxEntries = opts.MaxEntries;

        if (maxEntries > 0)
            logger.LogWarning(
                "MaxEntries={Max} — catalog is capped for debug mode", maxEntries);

        logger.LogInformation("Fetching filmliste from {Url}", url);

        // ── Download phase ────────────────────────────────────

        progress?.Report(new(CatalogFetchPhase.Downloading, PercentComplete: 0));

        using var response = await httpClient.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var networkStream = await response.Content.ReadAsStreamAsync(ct);

        // Wrap the stream to track download progress
        await using var trackingStream = totalBytes.HasValue
            ? new ProgressTrackingStream(networkStream, totalBytes.Value, pct =>
                progress?.Report(new(CatalogFetchPhase.Downloading, PercentComplete: pct)))
            : networkStream;

        // ── Parse phase ───────────────────────────────────────

        var episodes  = new List<Episode>();
        var channels  = new Dictionary<string, Channel>();
        var shows     = new Dictionary<string, Show>();
        var parsed    = 0L;
        // Filmliste typically has ~300k entries; we don't know exact count upfront
        const long estimatedTotal = 300_000;

        await foreach (var entry in parser.ParseAsync(trackingStream, ct))
        {
            var (episode, channel, show) = FilmlisteMapper.ToEpisode(entry);
            if (episode is null) continue;

            if (!channels.ContainsKey(channel.Id))
                channels[channel.Id] = channel;

            if (!shows.ContainsKey(show.Id))
            {
                show.Channel = channels[channel.Id];
                shows[show.Id] = show;
            }

            episode.Show = shows[show.Id];

            foreach (var stream in episode.Streams)
                stream.EpisodeId = episode.Id;

            episodes.Add(episode);
            parsed++;

            // Report parse progress every 1000 entries to avoid excessive updates
            if (parsed % 1_000 == 0)
                progress?.Report(new(
                    CatalogFetchPhase.Parsing,
                    EntriesParsed: parsed,
                    TotalEntries:  estimatedTotal));

            if (maxEntries > 0 && parsed >= maxEntries)
            {
                logger.LogInformation(
                    "MaxEntries cap reached ({Max}) — stopping parse early", maxEntries);
                break;
            }
        }

        // Final parse progress report
        progress?.Report(new(
            CatalogFetchPhase.Parsing,
            EntriesParsed: parsed,
            TotalEntries:  parsed));

        logger.LogInformation(
            "Filmliste ingested: {Episodes} episodes, {Shows} shows, {Channels} channels",
            episodes.Count, shows.Count, channels.Count);

        return episodes;
    }

    public async Task<Episode?> GetEpisodeDetailAsync(
        string episodeId, CancellationToken ct = default)
    {
        var all = await FetchCatalogAsync(progress: null, ct);
        return all.FirstOrDefault(e => e.Id == episodeId);
    }
}

// ──────────────────────────────────────────────────────────────
// Stream wrapper: tracks bytes read and reports download % 
// ──────────────────────────────────────────────────────────────

internal sealed class ProgressTrackingStream(
    Stream inner,
    long totalBytes,
    Action<int> onProgress) : Stream
{
    private long _bytesRead;
    private int  _lastReportedPct = -1;

    public override bool CanRead  => inner.CanRead;
    public override bool CanSeek  => false;
    public override bool CanWrite => false;
    public override long Length   => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = inner.Read(buffer, offset, count);
        ReportProgress(n);
        return n;
    }

    public override async Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var n = await inner.ReadAsync(buffer.AsMemory(offset, count), ct);
        ReportProgress(n);
        return n;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken ct = default)
    {
        var n = await inner.ReadAsync(buffer, ct);
        ReportProgress(n);
        return n;
    }

    private void ReportProgress(int bytesJustRead)
    {
        if (bytesJustRead <= 0) return;
        _bytesRead += bytesJustRead;
        var pct = (int)Math.Min(99, _bytesRead * 100 / totalBytes);
        if (pct == _lastReportedPct) return;
        _lastReportedPct = pct;
        onProgress(pct);
    }

    public override void Flush() => inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
