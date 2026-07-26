using System.Text;
using Krautwatch.Infrastructure.Crawling.Zdf;
using Shouldly;
using Xunit;

namespace Krautwatch.Live.Tests;

/// <summary>
/// Real-network tests against the live ZDF Mediathek API (search → episode → PTMD → MP4).
/// [Live] — excluded from the default/CI run. Run: ./build.cmd TestLive
/// </summary>
[Trait("Category", "Live")]
public class ZdfLiveTests
{
    private static readonly HttpClient Http = new();
    private static ZdfCatalogClient Client => new(Http);

    static ZdfLiveTests() =>
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Krautwatch/1.0 (+https://github.com/Chrison-dev/Krautwatch)");

    [Fact]
    public async Task Search_finds_HeuteShow_episodes()
    {
        var episodes = await Client.SearchEpisodesAsync("Heute Show");

        episodes.ShouldNotBeEmpty();
        episodes.ShouldContain(e => e.Title.Contains("heute-show vom", StringComparison.OrdinalIgnoreCase));
        episodes.ShouldAllBe(e => e.Canonical.StartsWith("/content/documents/"));
    }

    [Fact]
    public async Task Resolves_a_HeuteShow_progressive_MP4()
    {
        var episodes = await Client.SearchEpisodesAsync("Heute Show");
        var episode = episodes.First(e => e.Title.Contains("heute-show vom", StringComparison.OrdinalIgnoreCase));

        var stream = await Client.ResolveBestMp4Async(episode.Canonical);

        stream.ShouldNotBeNull();
        stream!.MimeType.ShouldContain("mp4");
        stream.Url.ShouldStartWith("https://");
        stream.Url.ShouldEndWith(stream.Url.Split('?')[0].Substring(stream.Url.LastIndexOf('/') + 1)); // has a filename
    }

    [Fact]
    public async Task Downloads_a_HeuteShow_episode()
    {
        // Real download of a real ZDF stream, bounded to ~5 MB so it's fast + small.
        // The production Downloader agent streams the whole file; this proves the pipeline.
        var episodes = await Client.SearchEpisodesAsync("Heute Show");
        var episode = episodes.First(e => e.Title.Contains("heute-show vom", StringComparison.OrdinalIgnoreCase));
        var stream = await Client.ResolveBestMp4Async(episode.Canonical);
        stream.ShouldNotBeNull();

        var path = Path.Combine(Path.GetTempPath(), $"krautwatch-heuteshow-{Guid.NewGuid():N}.mp4");
        try
        {
            const int cap = 5 * 1024 * 1024;
            using var resp = await Http.GetAsync(stream!.Url, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();

            await using (var source = await resp.Content.ReadAsStreamAsync())
            await using (var file = File.Create(path))
            {
                var buffer = new byte[81920];
                long total = 0; int read;
                while (total < cap && (read = await source.ReadAsync(buffer)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read));
                    total += read;
                }
            }

            var info = new FileInfo(path);
            info.Length.ShouldBeGreaterThan(1_000_000, "should have written real video bytes");
            // MP4 signature: an 'ftyp' box type at offset 4.
            var head = File.ReadAllBytes(path).AsSpan(0, 12);
            Encoding.ASCII.GetString(head.Slice(4, 4)).ShouldBe("ftyp");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
