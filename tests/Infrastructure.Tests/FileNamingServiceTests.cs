using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Enums;
using Krautwatch.Infrastructure.Jobs;
using Shouldly;
using Xunit;

namespace Krautwatch.Infrastructure.Tests;

/// <summary>
/// Where a finished download lands. The two layouts serve different consumers and neither can be dropped —
/// see <see cref="FileNamingService.BuildFinalPath"/>.
/// </summary>
public class FileNamingServiceTests
{
    private readonly FileNamingService _naming = new();
    private const string Dir = "/downloads";

    private static Episode Episode() => new()
    {
        Id = "zdf:1",
        Title = "heute-show vom 5. Juni 2026",
        ShowId = "zdf:heute-show",
        BroadcastDate = new DateTimeOffset(2026, 6, 5, 20, 0, 0, TimeSpan.Zero),
        Duration = TimeSpan.FromMinutes(40),
        Show = new Show
        {
            Id = "zdf:heute-show", Title = "heute-show", ChannelId = "zdf",
            Channel = new Channel { Id = "zdf", Name = "ZDF", ProviderKey = "zdf" },
        },
    };

    [Fact]
    public void An_arr_grab_lands_under_its_release_name()
    {
        // Sonarr parses the download's name to decide what it is, then renames the file itself. This is
        // the layout SABnzbd produces, and the one its importer expects.
        const string release = "heute-show.S2026E15.GERMAN.1080p.WEB.h264";

        var path = _naming.BuildFinalPath(Dir, Episode(), VideoQuality.High, releaseName: release);

        path.ShouldBe(Path.Combine(Dir, release, $"{release}.mp4"));
    }

    [Fact]
    public void A_ui_download_keeps_the_readable_library_layout()
    {
        // Nothing is going to rename these, so a release-style filename would be a poor thing to hand a
        // human browsing the folder.
        var path = _naming.BuildFinalPath(Dir, Episode(), VideoQuality.High);

        path.ShouldBe(Path.Combine(
            Dir, "ZDF", "heute-show", "heute-show - heute-show vom 5. Juni 2026 (2026-06-05).mp4"));
    }

    [Fact]
    public void The_library_layout_could_never_be_imported()
    {
        // Pins *why* the split exists: this filename carries no season or episode, so Sonarr can extract
        // nothing from it. Emitting it for an *arr grab is what made imports impossible.
        var path = _naming.BuildFinalPath(Dir, Episode(), VideoQuality.High);

        Path.GetFileName(path).ShouldNotContain("S2026");
        Path.GetFileName(path).ShouldNotContain("E15");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_release_name_falls_back_to_the_library_layout(string releaseName)
    {
        var path = _naming.BuildFinalPath(Dir, Episode(), VideoQuality.High, releaseName: releaseName);

        path.ShouldContain(Path.Combine("ZDF", "heute-show"));
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..\\..\\windows\\system32")]
    [InlineData("/etc/shadow")]
    public void A_release_name_cannot_escape_the_download_directory(string hostile)
    {
        // The release name arrives inside an NZB, which an operator could have hand-edited — untrusted
        // input that ends up in a filesystem path. Separators are mapped to underscores, so the result may
        // still *look* odd; what matters is that it resolves inside the download directory.
        var path = _naming.BuildFinalPath(Dir, Episode(), VideoQuality.High, releaseName: hostile);

        Path.GetFullPath(path).ShouldStartWith(Path.GetFullPath(Dir) + Path.DirectorySeparatorChar);
    }

    [Fact]
    public void A_release_name_that_sanitises_to_nothing_falls_back_to_the_library_layout()
    {
        // ".." trims to an empty string, and an empty path component would put a bare dotfile at the root
        // of the download directory.
        var path = _naming.BuildFinalPath(Dir, Episode(), VideoQuality.High, releaseName: "..");

        path.ShouldContain(Path.Combine("ZDF", "heute-show"));
    }

    [Fact]
    public void The_quality_suffix_only_applies_to_the_library_layout()
    {
        // A release name is already fully descriptive; appending "[SD]" would corrupt the string Sonarr
        // parses.
        const string release = "heute-show.S2026E15.GERMAN.480p.WEB.h264";

        var path = _naming.BuildFinalPath(Dir, Episode(), VideoQuality.Standard, releaseName: release);

        Path.GetFileName(path).ShouldBe($"{release}.mp4");
    }
}
