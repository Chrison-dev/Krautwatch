using System.Collections.Concurrent;
using System.Net;
using Krautwatch.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Krautwatch.Infrastructure.Downloads;

/// <summary>
/// Fetches an episode's WebVTT subtitle and writes it beside the finished video (#20).
/// </summary>
/// <remarks>
/// <para>
/// The sidecar is named <c>{video}.de.vtt</c>. Media servers match subtitles to a video by base name and
/// read the language from the middle segment, so <c>Show.S01E02.mkv</c> → <c>Show.S01E02.de.vtt</c> is
/// picked up automatically by Plex, Jellyfin and Sonarr's importer. German is hardcoded because every
/// broadcaster this project crawls is German-language; a second language would need the track's own
/// declared language rather than a guess.
/// </para>
/// <para>
/// <b>Best effort by design.</b> Nothing here throws: a subtitle that 404s, times out or is geo-blocked
/// must not fail a video that downloaded perfectly well. It logs and returns null.
/// </para>
/// </remarks>
public sealed class HttpSubtitleFetcher(IEgressProxyProvider egress, ILogger<HttpSubtitleFetcher> logger)
    : ISubtitleFetcher
{
    /// <summary>Subtitles are small; a short ceiling keeps a hung fetch from delaying the next download.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static readonly HttpClient Direct = CreateClient(null);
    private static readonly ConcurrentDictionary<string, HttpClient> Proxied = new();

    public async Task<string?> FetchAsync(
        string subtitleUrl,
        string videoPath,
        bool geoRestricted,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(subtitleUrl) || string.IsNullOrWhiteSpace(videoPath))
            return null;

        // A geo-restricted asset's captions sit on the same CDN, so they need the same egress. Trying
        // direct would fail in exactly the cases the proxy exists for.
        var candidates = geoRestricted
            ? (await SafeCandidatesAsync(ct)).Select(p => (string?)p).DefaultIfEmpty(null).ToList()
            : [null];

        foreach (var proxy in candidates)
        {
            try
            {
                var target = SidecarPathFor(videoPath);
                using var response = await ClientFor(proxy)
                    .GetAsync(subtitleUrl, HttpCompletionOption.ResponseHeadersRead, ct);

                if (!response.IsSuccessStatusCode)
                {
                    logger.LogInformation(
                        "Subtitle fetch returned {Status} for {Url}; continuing without subtitles.",
                        (int)response.StatusCode, subtitleUrl);
                    continue;
                }

                await using (var target_ = File.Create(target))
                    await response.Content.CopyToAsync(target_, ct);

                logger.LogInformation("Subtitle written to {Path}.", target);
                return target;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return null;   // shutting down; the video is what matters
            }
            catch (Exception ex)
            {
                logger.LogInformation(
                    ex, "Subtitle fetch failed for {Url}; continuing without subtitles.", subtitleUrl);
            }
        }

        return null;
    }

    /// <summary>
    /// <c>/path/Show.S01E02.mp4</c> → <c>/path/Show.S01E02.de.vtt</c>, so the sidecar keeps the video's
    /// base name — which is what media servers match on.
    /// </summary>
    internal static string SidecarPathFor(string videoPath)
    {
        var directory = Path.GetDirectoryName(videoPath) ?? "";
        var baseName = Path.GetFileNameWithoutExtension(videoPath);
        return Path.Combine(directory, $"{baseName}.de.vtt");
    }

    private async Task<IReadOnlyList<string>> SafeCandidatesAsync(CancellationToken ct)
    {
        try { return await egress.GetCandidatesAsync(ct); }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "Could not resolve an egress proxy for a subtitle fetch.");
            return [];
        }
    }

    private static HttpClient CreateClient(string? proxyUrl)
    {
        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15) };
        if (proxyUrl is not null)
        {
            handler.Proxy = new WebProxy(proxyUrl);
            handler.UseProxy = true;
        }

        var http = new HttpClient(handler) { Timeout = Timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Krautwatch/1.0 (+https://github.com/Chrison-dev/Krautwatch)");
        return http;
    }

    private static HttpClient ClientFor(string? proxyUrl) =>
        proxyUrl is null ? Direct : Proxied.GetOrAdd(proxyUrl, CreateClient);
}
