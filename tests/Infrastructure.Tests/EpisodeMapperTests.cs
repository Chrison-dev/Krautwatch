using Krautwatch.Domain.Enums;
using Krautwatch.Infrastructure.Crawling;
using Shouldly;
using Xunit;

namespace Krautwatch.Infrastructure.Tests;

public class EpisodeMapperTests
{
    private static EpisodeDetail Detail(string? stream = "https://cdn.ard.de/extra3.mp4", string? synopsis = "Satire.") =>
        new(
            Title: "extra 3 vom 10.07.2026",
            Show: "extra 3",
            Broadcaster: "NDR",
            AirDate: new DateTimeOffset(2026, 7, 10, 22, 0, 0, TimeSpan.Zero),
            Duration: TimeSpan.FromMinutes(44),
            Synopsis: synopsis,
            StreamUrl: stream,
            SubtitleUrl: null);

    [Fact]
    public void Channel_and_show_ids_are_deterministic_from_provider_and_title()
    {
        var channel = EpisodeMapper.Channel("ard", "ARD");
        var show = EpisodeMapper.Show("ard", "extra 3", channel);

        channel.Id.ShouldBe("ard");
        channel.ProviderKey.ShouldBe("ard");
        channel.Name.ShouldBe("ARD");
        show.Id.ShouldBe("ard:extra-3");
        show.ChannelId.ShouldBe("ard");
        show.Channel.ShouldBeSameAs(channel);
    }

    [Fact]
    public void Episode_id_is_provider_scoped_native_id_with_a_single_mp4_stream()
    {
        var channel = EpisodeMapper.Channel("ard", "ARD");
        var show = EpisodeMapper.Show("ard", "extra 3", channel);

        var episode = EpisodeMapper.Episode("ard", show, "abc123", Detail());

        episode.Id.ShouldBe("ard:abc123");
        episode.ShowId.ShouldBe("ard:extra-3");
        episode.Show.ShouldBeSameAs(show);
        episode.Title.ShouldBe("extra 3 vom 10.07.2026");
        episode.Description.ShouldBe("Satire.");
        episode.BroadcastDate.ShouldBe(new DateTimeOffset(2026, 7, 10, 22, 0, 0, TimeSpan.Zero));
        episode.Duration.ShouldBe(TimeSpan.FromMinutes(44));

        var streamRow = episode.Streams.ShouldHaveSingleItem();
        streamRow.Id.ShouldBe("ard:abc123:v");
        streamRow.EpisodeId.ShouldBe("ard:abc123");
        streamRow.Url.ShouldBe("https://cdn.ard.de/extra3.mp4");
        streamRow.Format.ShouldBe("mp4");
        streamRow.Quality.ShouldBe(VideoQuality.High);
    }

    [Fact]
    public void Episode_without_a_stream_has_no_stream_rows()
    {
        var channel = EpisodeMapper.Channel("zdf", "ZDF");
        var show = EpisodeMapper.Show("zdf", "heute-show", channel);

        var episode = EpisodeMapper.Episode("zdf", show, "doc/1", Detail(stream: null));

        episode.Streams.ShouldBeEmpty();
    }

    [Fact]
    public void Synopsis_longer_than_the_column_limit_is_truncated()
    {
        var channel = EpisodeMapper.Channel("ard", "ARD");
        var show = EpisodeMapper.Show("ard", "extra 3", channel);

        var episode = EpisodeMapper.Episode("ard", show, "x", Detail(synopsis: new string('a', 6000)));

        episode.Description!.Length.ShouldBe(5000);
    }
}
