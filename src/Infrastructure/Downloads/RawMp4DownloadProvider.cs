using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Interfaces;
using Krautwatch.Infrastructure.Jobs;
using Microsoft.Extensions.Logging;

namespace Krautwatch.Infrastructure.Downloads;

/// <summary>
/// Pulls a job's stream to disk as a <b>raw</b> progressive-MP4 copy — the exact bytes the Mediathek
/// serves, no transcoding, no ffmpeg (HLS remux is a later orchestration step, DR-010). Streams to a
/// <c>.part</c> temp file and atomically moves it into the structured library path on success.
/// </summary>
public sealed class RawMp4DownloadProvider(FileNamingService naming, ILogger<RawMp4DownloadProvider> logger)
    : IDownloadProvider
{
    // A dedicated client with no timeout: downloads are long and cancellation is driven by the token.
    // Deliberately not from IHttpClientFactory — that path carries ServiceDefaults' standard resilience
    // handler, whose total-request timeout would abort a large streaming download. A User-Agent is
    // required: the Mediathek CDNs 403 UA-less requests.
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Krautwatch/1.0 (+https://github.com/Chrison-dev/Krautwatch)");
        return http;
    }

    public async Task<DownloadResult> DownloadAsync(
        DownloadJob job, string outputDirectory, IProgress<double> progress, CancellationToken ct = default)
    {
        var episode = job.Episode
            ?? throw new InvalidOperationException($"Job {job.Id} has no episode metadata to name the file.");

        var tempPath  = naming.BuildTempPath(outputDirectory, job.Id, job.Quality);
        var finalPath = naming.BuildFinalPath(outputDirectory, episode, job.Quality);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        logger.LogInformation("Downloading {Episode} → {Path}", episode.Title, finalPath);

        using (var response = await Http.GetAsync(job.StreamUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var contentLength = response.Content.Headers.ContentLength;

            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var file = File.Create(tempPath);

            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                total += read;
                if (contentLength is > 0)
                    progress.Report(Math.Clamp(total * 100.0 / contentLength.Value, 0, 100));
            }
        }

        File.Move(tempPath, finalPath, overwrite: true);
        var size = new FileInfo(finalPath).Length;
        logger.LogInformation("Downloaded {Episode} ({Size} bytes)", episode.Title, size);
        return new DownloadResult(finalPath, size);
    }
}
