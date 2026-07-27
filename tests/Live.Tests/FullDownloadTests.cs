using System.Net;
using System.Text;
using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Enums;
using Krautwatch.Domain.Interfaces;
using Krautwatch.Infrastructure.Crawling;
using Krautwatch.Infrastructure.Crawling.Ard;
using Krautwatch.Infrastructure.Crawling.Zdf;
using Krautwatch.Infrastructure.Downloads;
using Krautwatch.Infrastructure.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Krautwatch.Live.Tests;

/// <summary>
/// Downloads ONE real FULL episode per show through the production <see cref="RawMp4DownloadProvider"/>
/// — resolve the stream, pull the whole file to disk, assert it's a genuine MP4, then clean up. These
/// pull hundreds of MB and take a while, so they're [Live] (excluded from CI). Run locally:
///   ./build.cmd TestLive
/// Reuses the three shows from the crawler work: ARD "Extra 3", KiKA "Die Biene Maja", ZDF "heute-show".
/// </summary>
[Trait("Category", "Live")]
public class FullDownloadTests
{
    private static readonly HttpClient Http = new();

    static FullDownloadTests() =>
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Krautwatch/1.0 (+https://github.com/Chrison-dev/Krautwatch)");

    [Fact]
    public async Task Downloads_a_full_Extra3_episode_from_ARD()
    {
        var ard = new ArdCatalogClient(Http);
        var show = await ard.FindShowAsync("Extra 3", ct: TestContext.Current.CancellationToken);
        var full = (await ard.GetFullEpisodesAsync(show!, TestContext.Current.CancellationToken))
            .First(e => e.Title.Contains("extra 3 vom", StringComparison.OrdinalIgnoreCase));
        await DownloadAndVerifyAsync(await ard.FetchEpisodeDetailAsync(full, TestContext.Current.CancellationToken));
    }

    // A DE egress proxy for local runs (#45). Set it to do the REAL geo-restricted download; leave it
    // unset and the geo-restricted case just proves the fail-fast. e.g.
    //   KRAUTWATCH_TEST_PROXY=http://<de-host>:3128 ./build.cmd TestLive
    private static readonly string? TestProxy = Environment.GetEnvironmentVariable("KRAUTWATCH_TEST_PROXY");

    [Fact]
    public async Task Downloads_a_full_BieneMaja_episode_from_KiKA()
    {
        var ard = new ArdCatalogClient(Http);
        var show = await ard.FindShowAsync("Biene Maja", client: "kika", TestContext.Current.CancellationToken);
        var full = (await ard.GetFullEpisodesAsync(show!, TestContext.Current.CancellationToken)).First();
        // Biene Maja is DACH geo-fenced. With a DE egress (KRAUTWATCH_TEST_PROXY) it downloads for real;
        // without one, the provider fails fast (geo-restricted + no egress) — tolerated here.
        await DownloadAndVerifyAsync(await ard.FetchEpisodeDetailAsync(full, TestContext.Current.CancellationToken),
            tolerateGeoBlock: string.IsNullOrWhiteSpace(TestProxy));
    }

    [Fact]
    public async Task Downloads_a_full_HeuteShow_episode_from_ZDF()
    {
        var zdf = new ZdfCatalogClient(Http);
        var full = (await zdf.SearchEpisodesAsync("Heute Show", TestContext.Current.CancellationToken))
            .First(e => e.Title.Contains("heute-show vom", StringComparison.OrdinalIgnoreCase));
        await DownloadAndVerifyAsync(await zdf.FetchEpisodeDetailAsync(full, TestContext.Current.CancellationToken));
    }

    // ── shared: run the real provider, assert a genuine MP4 landed, then clean up ──
    private static async Task DownloadAndVerifyAsync(EpisodeDetail? detail, bool tolerateGeoBlock = false)
    {
        detail.ShouldNotBeNull();
        detail!.StreamUrl.ShouldNotBeNull();

        var job = new DownloadJob
        {
            Id            = Guid.NewGuid(),
            EpisodeId     = "live-test",
            Episode       = EpisodeFor(detail),
            StreamUrl     = detail.StreamUrl!,
            Quality       = VideoQuality.High,
            GeoRestricted = detail.GeoRestricted,
        };

        var directory = Path.Combine(Path.GetTempPath(), $"krautwatch-dl-{Guid.NewGuid():N}");
        try
        {
            var provider = new RawMp4DownloadProvider(new FileNamingService(), new EnvEgress(), NullLogger<RawMp4DownloadProvider>.Instance);

            DownloadResult result;
            try
            {
                result = await provider.DownloadAsync(job, directory, new Progress<double>(), CancellationToken.None);
            }
            catch (HttpRequestException ex) when (tolerateGeoBlock && ex.StatusCode == HttpStatusCode.Forbidden)
            {
                return; // CDN geo-block from this location — the download path itself is fine
            }
            catch (InvalidOperationException) when (tolerateGeoBlock)
            {
                return; // geo-restricted + no egress configured → fail-fast (expected without a test proxy)
            }

            File.Exists(result.OutputPath).ShouldBeTrue();
            result.SizeBytes.ShouldBeGreaterThan(5_000_000, "a full episode is many MB");
            IsMp4(result.OutputPath).ShouldBeTrue("the downloaded file must be a real MP4 (ftyp box)");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static Episode EpisodeFor(EpisodeDetail detail) => new()
    {
        Id = "live-test",
        Title = detail.Title,
        ShowId = "live-show",
        Show = new Show
        {
            Id = "live-show",
            Title = detail.Show,
            ChannelId = "live",
            Channel = new Channel { Id = "live", Name = detail.Broadcaster, ProviderKey = "live" },
        },
        BroadcastDate = detail.AirDate ?? DateTimeOffset.UtcNow,
        Duration = detail.Duration,
    };

    private static bool IsMp4(string path)
    {
        Span<byte> head = stackalloc byte[12];
        using var fs = File.OpenRead(path);
        return fs.Read(head) == 12 && Encoding.ASCII.GetString(head.Slice(4, 4)) == "ftyp";
    }

    // Egress that offers the optional KRAUTWATCH_TEST_PROXY (a DE proxy) for geo-restricted downloads.
    private sealed class EnvEgress : IEgressProxyProvider
    {
        public Task<IReadOnlyList<string>> GetCandidatesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(string.IsNullOrWhiteSpace(TestProxy) ? [] : [TestProxy!]);

        public Task ReportResultAsync(string proxyUrl, bool ok, CancellationToken ct = default) => Task.CompletedTask;
    }
}
