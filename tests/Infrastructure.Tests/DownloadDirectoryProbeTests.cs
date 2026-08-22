using Krautwatch.Infrastructure.Downloads;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Krautwatch.Infrastructure.Tests;

/// <summary>
/// The setup wizard's "can the downloader write there?" check (#100). Against the real file system,
/// because the whole point is that permissions and mounts are not what they claim to be.
/// </summary>
public class DownloadDirectoryProbeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kw-probe-" + Guid.NewGuid().ToString("N"));
    private readonly DownloadDirectoryProbe _sut = new(NullLogger<DownloadDirectoryProbe>.Instance);

    public DownloadDirectoryProbeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void A_writable_directory_reports_writable()
    {
        var status = _sut.Check(_dir);

        status.Exists.ShouldBeTrue();
        status.Writable.ShouldBeTrue();
        status.Path.ShouldBe(Path.GetFullPath(_dir));
    }

    [Fact]
    public void The_probe_leaves_nothing_behind_in_the_library()
    {
        _sut.Check(_dir);

        // A stray dotfile in someone's media directory is a small thing that looks like a bug forever.
        Directory.GetFileSystemEntries(_dir).ShouldBeEmpty();
    }

    [Fact]
    public void A_missing_directory_says_so_and_points_at_the_mount()
    {
        var status = _sut.Check(Path.Combine(_dir, "not-mounted"));

        status.Exists.ShouldBeFalse();
        status.Writable.ShouldBeFalse();
        // The overwhelmingly common cause in a container, and the operator can act on it.
        status.Message.ShouldContain("KRAUTWATCH_DOWNLOADS");
    }

    [Fact]
    public void A_read_only_directory_is_reported_as_existing_but_not_writable()
    {
        if (OperatingSystem.IsWindows()) return;   // chmod semantics differ; the path this guards is Linux containers

        var readOnly = Path.Combine(_dir, "read-only");
        Directory.CreateDirectory(readOnly);
        File.SetUnixFileMode(readOnly, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try
        {
            var status = _sut.Check(readOnly);

            // The case a permissions inspection would get wrong and a real write does not: the mount is
            // there, looks fine, and cannot be written to.
            status.Exists.ShouldBeTrue();
            status.Writable.ShouldBeFalse();
            status.Message.ShouldContain("cannot write");
        }
        finally
        {
            File.SetUnixFileMode(readOnly,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void An_unconfigured_directory_is_not_an_exception()
    {
        var status = _sut.Check("");

        status.Writable.ShouldBeFalse();
        status.Message.ShouldContain("No download directory");
    }
}
