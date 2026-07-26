using Krautwatch.Infrastructure.Downloads;
using Shouldly;
using Xunit;

namespace Krautwatch.Infrastructure.Tests;

public class DownloadDispatcherTests
{
    [Theory]
    [InlineData("https://cdn.ard.de/master.m3u8")]
    [InlineData("https://cdn.zdf.de/hls/playlist.m3u8?token=abc")]
    [InlineData("https://x/AUDIO.M3U8")]
    public void Hls_urls_route_to_ffmpeg(string url) => DownloadDispatcher.IsHls(url).ShouldBeTrue();

    [Theory]
    [InlineData("https://cdn.ard.de/extra3_hd.mp4")]                       // progressive MP4
    [InlineData("https://nrodlzdf-a.akamaihd.net/dach/tivi/171124_maj_3")] // ZDF extension-less progressive
    [InlineData("https://x/video.mp4?list=nope")]
    public void Non_hls_urls_route_to_the_raw_puller(string url) => DownloadDispatcher.IsHls(url).ShouldBeFalse();
}
