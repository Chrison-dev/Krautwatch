using System.Collections.Concurrent;
using System.Net;
using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Interfaces;
using Krautwatch.Infrastructure.Jobs;
using Microsoft.Extensions.Logging;

namespace Krautwatch.Infrastructure.Downloads;

/// <summary>
/// Pulls a job's stream to disk as a <b>raw</b> progressive-MP4 copy — the exact bytes the Mediathek
/// serves, no transcoding, no ffmpeg (HLS remux is a later orchestration step, DR-010). Streams to a
/// <c>.part</c> temp file and atomically moves it into the structured library path on success.
///
/// A geo-restricted job (#45) is fetched through an egress proxy: candidates are tried best-first and
/// a failed handshake falls through to the next; a geo-restricted job with no egress configured fails
/// fast. Non-restricted jobs fetch directly, exactly as before.
/// </summary>
public sealed class RawMp4DownloadProvider(
    FileNamingService naming, IEgressProxyProvider egress, ILogger<RawMp4DownloadProvider> logger)
    : IDownloadProvider
{
    // Dedicated clients with no timeout: downloads are long and cancellation is driven by the token.
    // Deliberately not from IHttpClientFactory — that path carries ServiceDefaults' standard resilience
    // handler, whose total-request timeout would abort a large streaming download. A User-Agent is
    // required: the Mediathek CDNs 403 UA-less requests. One direct client + one per egress proxy.
    private static readonly HttpClient Direct = CreateClient(null);
    private static readonly ConcurrentDictionary<string, HttpClient> Proxied = new();

    /// <summary>
    /// How long the stream may deliver nothing before the download is treated as dead.
    /// </summary>
    /// <remarks>
    /// The client timeout is infinite because a total-request deadline would abort a legitimate
    /// multi-gigabyte download. That leaves the opposite failure: a CDN that stops sending without closing
    /// the connection, where <c>ReadAsync</c> simply never returns. Observed against ZDF mid-download — the
    /// job sat at 40% forever, and Sonarr waits on the download client indefinitely, so the whole grab
    /// silently wedges. An idle deadline is the bound that actually fits: unlimited total time, but data
    /// has to keep arriving.
    /// </remarks>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    private static HttpClient CreateClient(string? proxyUrl)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(30),
        };
        if (proxyUrl is not null)
        {
            handler.Proxy = new WebProxy(proxyUrl);
            handler.UseProxy = true;
        }
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Krautwatch/1.0 (+https://github.com/Chrison-dev/Krautwatch)");
        return http;
    }

    private static HttpClient ClientFor(string? proxyUrl) =>
        proxyUrl is null ? Direct : Proxied.GetOrAdd(proxyUrl, CreateClient);

    public async Task<DownloadResult> DownloadAsync(
        DownloadJob job, string outputDirectory, IProgress<double> progress, CancellationToken ct = default)
    {
        var episode = job.Episode
            ?? throw new InvalidOperationException($"Job {job.Id} has no episode metadata to name the file.");

        var attempts = await ResolveEgressAsync(job, ct);

        var tempPath  = naming.BuildTempPath(outputDirectory, job.Id, job.Quality);
        var finalPath = naming.BuildFinalPath(outputDirectory, episode, job.Quality, releaseName: job.ReleaseName);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        logger.LogInformation("Downloading {Episode} → {Path}", episode.Title, finalPath);

        var (response, usedProxy) = await OpenAsync(job.StreamUrl, attempts, episode.Title, ct);

        try
        {
            using (response)
            {
                var contentLength = response.Content.Headers.ContentLength;

                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var file = File.Create(tempPath);

                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await ReadWithStallGuardAsync(source, buffer, episode.Title, total, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    total += read;
                    if (contentLength is > 0)
                        progress.Report(Math.Clamp(total * 100.0 / contentLength.Value, 0, 100));
                }

                if (contentLength is > 0 && total < contentLength.Value)
                {
                    // The stream ended early. Without this the truncated file would be moved into place and
                    // reported as a success, and Sonarr would import a partial episode.
                    throw new IOException(
                        $"Stream ended after {total} of {contentLength.Value} bytes for \"{episode.Title}\".");
                }
            }
        }
        catch
        {
            TryDelete(tempPath); // don't leave a half-written .part on failure/cancel
            throw;
        }

        File.Move(tempPath, finalPath, overwrite: true);
        var size = new FileInfo(finalPath).Length;
        logger.LogInformation("Downloaded {Episode} ({Size} bytes) via {Egress}",
            episode.Title, size, usedProxy ?? "direct");
        return new DownloadResult(finalPath, size);
    }

    /// <summary>
    /// One read, bounded by <see cref="StallTimeout"/>.
    /// </summary>
    /// <remarks>
    /// The cancellation is linked so a genuine cancel (an operator removing the download) still propagates
    /// as <see cref="OperationCanceledException"/>, while an idle stream surfaces as an
    /// <see cref="IOException"/> the job can fail and Sonarr can retry. Distinguishing the two matters:
    /// a cancelled job must not be reported as a failed one.
    /// </remarks>
    private async Task<int> ReadWithStallGuardAsync(
        Stream source, byte[] buffer, string episodeTitle, long bytesSoFar, CancellationToken ct)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(StallTimeout);

        try
        {
            return await source.ReadAsync(buffer, idle.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(
                "Download of {Episode} stalled after {Bytes} bytes — no data for {Seconds}s",
                episodeTitle, bytesSoFar, StallTimeout.TotalSeconds);

            throw new IOException(
                $"Download stalled: no data for {StallTimeout.TotalSeconds:F0}s after {bytesSoFar} bytes.");
        }
    }

    // The egress candidates to try, in order. Direct ([null]) for an unrestricted job; the proxy
    // candidates for a geo-restricted one — or fail fast if none are configured.
    private async Task<IReadOnlyList<string?>> ResolveEgressAsync(DownloadJob job, CancellationToken ct)
    {
        if (!job.GeoRestricted) return [null];

        var proxies = await egress.GetCandidatesAsync(ct);
        if (proxies.Count == 0)
            throw new InvalidOperationException(
                "Stream is geo-restricted (DACH-only) and no egress proxy is configured. " +
                "Set Download:ProxyUrl, or enable Download:ProxyList, to route it through a German egress (#45).");

        return proxies.Cast<string?>().ToList();
    }

    // Opens the stream, trying each egress candidate best-first; a failed handshake falls through to
    // the next. Once headers arrive we commit to that egress (a mid-stream failure fails the job, and
    // a retry re-selects). Cancellation propagates immediately.
    private async Task<(HttpResponseMessage Response, string? Proxy)> OpenAsync(
        string url, IReadOnlyList<string?> attempts, string episodeTitle, CancellationToken ct)
    {
        Exception? lastError = null;
        foreach (var proxy in attempts)
        {
            try
            {
                var resp = await ClientFor(proxy).GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                if (proxy is not null) await egress.ReportResultAsync(proxy, ok: true, ct);
                return (resp, proxy);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                if (proxy is not null) await egress.ReportResultAsync(proxy, ok: false, ct);
                logger.LogWarning("Egress '{Egress}' failed for {Episode}: {Error}",
                    proxy ?? "direct", episodeTitle, ex.Message);
            }
        }

        throw new InvalidOperationException(
            $"All egress attempts failed for '{episodeTitle}'.", lastError);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
