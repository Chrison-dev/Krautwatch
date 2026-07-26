using System.Text;
using Shouldly;

namespace Krautwatch.Live.Tests;

internal static class Download
{
    /// <summary>
    /// Downloads a stream RAW — no transcoding, no ffmpeg: a straight byte copy of the exact file
    /// the Mediathek serves (progressive MP4). Bounded to ~cap bytes so tests stay fast + small
    /// (the production Downloader agent streams the whole file; any HLS remux is a later
    /// orchestration step). Verifies the bytes are a real MP4 (the 'ftyp' box), then cleans up.
    /// </summary>
    public static async Task VerifyRawMp4Async(HttpClient http, string url, int cap = 5 * 1024 * 1024)
    {
        var path = Path.Combine(Path.GetTempPath(), $"krautwatch-dl-{Guid.NewGuid():N}.mp4");
        try
        {
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
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
            var head = File.ReadAllBytes(path).AsSpan(0, 12);
            Encoding.ASCII.GetString(head.Slice(4, 4)).ShouldBe("ftyp");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
