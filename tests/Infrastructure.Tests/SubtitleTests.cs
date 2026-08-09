using System.Text.Json;
using Krautwatch.Infrastructure.Crawling.Zdf;
using Krautwatch.Infrastructure.Downloads;
using Shouldly;
using Xunit;

namespace Krautwatch.Infrastructure.Tests;

/// <summary>
/// Covers subtitle support (#20): picking the WebVTT track out of a PTMD document, and naming the
/// sidecar so media servers actually pair it with the video.
/// </summary>
public class SubtitleTests
{
    private static JsonElement Ptmd(string json) => JsonDocument.Parse(json).RootElement;

    // ── ZDF caption selection ─────────────────────────────────

    [Fact]
    public void WebVtt_is_preferred_over_the_xml_variant()
    {
        // ZDF publishes each caption several ways. Only WebVTT is useful as a sidecar; handing a media
        // server EBU-TT-D XML named .vtt would be worse than no subtitle at all.
        var ptmd = Ptmd("""
        {
          "captions": [
            { "uri": "https://zdf.example/cap.xml", "format": "ebu-tt-d-basic-de", "language": "deu" },
            { "uri": "https://zdf.example/cap.vtt", "format": "webvtt", "language": "deu" }
          ]
        }
        """);

        ZdfCatalogClient.FindWebVtt(ptmd).ShouldBe("https://zdf.example/cap.vtt");
    }

    [Fact]
    public void A_vtt_uri_is_accepted_when_the_format_is_spelled_differently()
    {
        // Lenient on purpose: a rename on ZDF's side should degrade to "no subtitles", not to writing
        // the wrong format under a .vtt name.
        var ptmd = Ptmd("""
        { "captions": [ { "uri": "https://zdf.example/cap.vtt", "format": "WebVTT-Ergaenzung" } ] }
        """);

        ZdfCatalogClient.FindWebVtt(ptmd).ShouldBe("https://zdf.example/cap.vtt");
    }

    [Fact]
    public void No_webvtt_track_yields_nothing_rather_than_the_xml()
    {
        var ptmd = Ptmd("""
        { "captions": [ { "uri": "https://zdf.example/cap.xml", "format": "ebu-tt-d-basic-de" } ] }
        """);

        ZdfCatalogClient.FindWebVtt(ptmd).ShouldBeNull();
    }

    [Theory]
    [InlineData("""{ "captions": [] }""")]
    [InlineData("""{ "captions": null }""")]
    [InlineData("""{ }""")]
    [InlineData("""{ "captions": [ { "format": "webvtt" } ] }""")]   // no uri
    public void Absent_or_malformed_captions_are_not_an_error(string json) =>
        ZdfCatalogClient.FindWebVtt(Ptmd(json)).ShouldBeNull();

    // ── sidecar naming ────────────────────────────────────────

    [Theory]
    [InlineData("/downloads/Show.S01E02.mp4", "/downloads/Show.S01E02.de.vtt")]
    [InlineData("/downloads/Show.S01E02.mkv", "/downloads/Show.S01E02.de.vtt")]
    [InlineData("/m/ARD/heute-show/heute-show - x (2026-06-05).mp4",
                "/m/ARD/heute-show/heute-show - x (2026-06-05).de.vtt")]
    public void The_sidecar_keeps_the_videos_base_name(string video, string expected)
    {
        // Media servers pair a subtitle to a video by base name and read the language from the middle
        // segment, so this exact shape is what makes Plex, Jellyfin and Sonarr pick it up unaided.
        HttpSubtitleFetcher.SidecarPathFor(video).ShouldBe(expected.Replace('/', Path.DirectorySeparatorChar));
    }
}
