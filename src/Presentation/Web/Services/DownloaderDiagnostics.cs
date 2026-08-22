using System.Net.Http.Json;
using Krautwatch.Infrastructure.Downloads;

namespace Krautwatch.Web.Services;

/// <summary>
/// Asks the Downloader agent whether it can write to a download directory (#100).
/// </summary>
/// <remarks>
/// <para>
/// The check cannot run here. Only the Downloader mounts the media — deliberately, so that a service
/// which never writes files cannot corrupt the library — so the Web host would fail the check on a
/// perfectly correct install. That is exactly what the wizard's original button did, and why it was
/// removed rather than left to mislead people.
/// </para>
/// <para>
/// Reached by service discovery: <c>http://agent-downloader</c> resolves in the dev fleet and on the
/// compose network, and nowhere else — the agent publishes no external endpoint.
/// </para>
/// </remarks>
public sealed class DownloaderDiagnostics(HttpClient http, ILogger<DownloaderDiagnostics> logger)
{
    /// <param name="path">
    /// The path to test. The wizard passes what the operator has just typed, so it can be checked
    /// before it is saved.
    /// </param>
    public async Task<DownloadDirectoryStatus> CheckDirectoryAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var status = await http.GetFromJsonAsync<DownloadDirectoryStatus>(
                $"/diagnostics/download-directory?path={Uri.EscapeDataString(path)}", ct);

            return status ?? Unreachable(path, "The downloader answered with nothing.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not reach the downloader to check '{Path}'.", path);

            // Distinct from "not writable" on purpose: one means fix your mount, the other means the
            // downloader is not running — and an operator mid-setup can very easily be in the second
            // state, with nothing wrong with the path at all.
            return Unreachable(path,
                "Could not reach the downloader service, so this path could not be checked. " +
                "That is expected if it is not running yet.");
        }
    }

    private static DownloadDirectoryStatus Unreachable(string path, string message) =>
        new(path, false, false, message);
}
