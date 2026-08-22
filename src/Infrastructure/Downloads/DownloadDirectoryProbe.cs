using Microsoft.Extensions.Logging;

namespace Krautwatch.Infrastructure.Downloads;

/// <summary>
/// What a write probe found: the path as the downloader resolved it, and whether it can actually
/// write there.
/// </summary>
/// <param name="Path">The absolute path tested, as the downloader process sees it.</param>
/// <param name="Exists">Whether the directory exists at all.</param>
/// <param name="Writable">Whether a file was created and removed there just now.</param>
/// <param name="Message">One line an operator can act on.</param>
public sealed record DownloadDirectoryStatus(string Path, bool Exists, bool Writable, string Message);

/// <summary>
/// Answers "can the downloader write where it has been told to?" — from inside the downloader, which
/// is the only process that mounts the media (#100).
/// </summary>
/// <remarks>
/// It writes and deletes a real file rather than inspecting permissions. A container bind mount can
/// report perfectly sensible modes and still be read-only, and the wizard is asked precisely because
/// somebody's mount is wrong; anything short of a real write would confirm the wrong thing.
/// </remarks>
public sealed class DownloadDirectoryProbe(ILogger<DownloadDirectoryProbe> logger)
{
    private const string ProbeFileName = ".krautwatch-write-test";

    public DownloadDirectoryStatus Check(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new DownloadDirectoryStatus("", false, false, "No download directory is configured.");

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new DownloadDirectoryStatus(path, false, false, $"'{path}' is not a usable path.");
        }

        if (!Directory.Exists(full))
        {
            return new DownloadDirectoryStatus(full, false, false,
                $"The downloader cannot see {full}. In Docker this usually means the volume is not " +
                "mounted — check KRAUTWATCH_DOWNLOADS and that the container has the mount.");
        }

        var probe = Path.Combine(full, ProbeFileName);

        try
        {
            File.WriteAllText(probe, "krautwatch");
            File.Delete(probe);

            return new DownloadDirectoryStatus(full, true, true,
                $"The downloader can write to {full}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Download directory {Path} is not writable.", full);

            return new DownloadDirectoryStatus(full, true, false,
                $"{full} exists but the downloader cannot write to it ({ex.GetType().Name}). " +
                "Check the mount is not read-only and that the container user owns it.");
        }
        finally
        {
            // A crash between write and delete would otherwise leave the probe behind in the library.
            try { if (File.Exists(probe)) File.Delete(probe); } catch { /* best effort */ }
        }
    }
}
