using Krautwatch.Api.NewznabIndexerApi.Auth;
using Krautwatch.Application.Downloads;
using Krautwatch.Domain.Interfaces;
using Microsoft.AspNetCore.WebUtilities;

namespace Krautwatch.Api.NewznabIndexerApi.Endpoints;

/// <summary>
/// The SABnzbd-compatible download-client API Sonarr/Radarr drive: <c>?mode=version|get_config|
/// addfile|addurl|queue|history</c>. Adds resolve the opaque token (from the synthetic NZB the
/// indexer served) into a <see cref="DownloadJob"/>; queue/history project the job state back.
/// </summary>
public static class SabnzbdEndpoints
{
    private const string SabVersion = "4.3.0";

    public static IEndpointRouteBuilder MapSabnzbdEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapMethods("/sabnzbd/api", ["GET", "POST"], HandleAsync).WithName("Sabnzbd");
        return app;
    }

    private static async Task<IResult> HandleAsync(
        HttpContext http,
        IConfiguration config,
        AddDownloadByTokenHandler add,
        GetDownloadQueueHandler queue,
        CancelDownloadHandler cancel,
        ISettingsRepository settings,
        CancellationToken ct)
    {
        var query = http.Request.Query;

        if (!ApiKeyGuard.IsAuthorized(config, query["apikey"]))
            return Results.Json(new { status = false, error = "API Key Incorrect" });

        switch (query["mode"].ToString().ToLowerInvariant())
        {
            case "version":
                return Results.Json(new { version = SabVersion });

            case "get_config":
                var appSettings = await settings.GetAsync(ct);
                return Results.Json(new
                {
                    config = new
                    {
                        misc = new { complete_dir = appSettings.DownloadDirectory, version = SabVersion },
                        categories = new[] { "*", "tv", "movies" },
                    }
                });

            case "addfile":
                return await AddAsync(add, await ReadTokenFromFileAsync(http, ct), ct);

            case "addurl":
                return await AddAsync(add, TokenFromUrl(query["name"]), ct);

            case "queue":
                if (IsDelete(query))
                {
                    if (Guid.TryParse(query["value"], out var id)) await cancel.HandleAsync(id, ct);
                    return Results.Json(new { status = true });
                }
                return Results.Json(new { queue = new { paused = false, slots = await QueueSlotsAsync(queue, ct) } });

            case "history":
                if (IsDelete(query))
                    return Results.Json(new { status = true });
                return Results.Json(new { history = new { slots = await HistorySlotsAsync(queue, ct) } });

            default:
                return Results.Json(new { status = false, error = $"Unknown mode '{query["mode"]}'" });
        }
    }

    private static bool IsDelete(IQueryCollection query) =>
        string.Equals(query["name"], "delete", StringComparison.OrdinalIgnoreCase);

    private static async Task<IResult> AddAsync(AddDownloadByTokenHandler add, string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
            return Results.Json(new { status = false, error = "No download token." });

        var jobId = await add.HandleAsync(token, ct);
        return jobId is null
            ? Results.Json(new { status = false, error = "Unknown release." })
            : Results.Json(new { status = true, nzo_ids = new[] { jobId.Value.ToString() } });
    }

    private static async Task<string?> ReadTokenFromFileAsync(HttpContext http, CancellationToken ct)
    {
        if (!http.Request.HasFormContentType) return null;
        var form = await http.Request.ReadFormAsync(ct);
        var file = form.Files.FirstOrDefault();
        if (file is null) return null;

        await using var stream = file.OpenReadStream();
        using var reader = new StreamReader(stream);
        return NzbToken.Read(await reader.ReadToEndAsync(ct));
    }

    private static string? TokenFromUrl(string? nameOrUrl)
    {
        if (string.IsNullOrWhiteSpace(nameOrUrl) || !Uri.TryCreate(nameOrUrl, UriKind.Absolute, out var uri))
            return null;
        var token = QueryHelpers.ParseQuery(uri.Query).TryGetValue("token", out var v) ? v.ToString() : null;
        return string.IsNullOrEmpty(token) ? null : token;
    }

    private static async Task<List<object>> QueueSlotsAsync(GetDownloadQueueHandler queue, CancellationToken ct)
    {
        var jobs = await queue.HandleAsync(ct);
        return jobs.Where(j => j.Active).Select((j, i) => (object)new
        {
            nzo_id     = j.JobId.ToString(),
            filename   = DisplayName(j),
            cat        = "tv",
            status     = j.Status == "Queued" ? "Queued" : "Downloading",
            percentage = ((int)(j.ProgressPercent ?? 0)).ToString(),
            mb         = Megabytes(j.FileSizeBytes),
            mbleft     = "0",
            timeleft   = "0:00:00",
            priority   = "Normal",
            index      = i,
        }).ToList();
    }

    private static async Task<List<object>> HistorySlotsAsync(GetDownloadQueueHandler queue, CancellationToken ct)
    {
        var jobs = await queue.HandleAsync(ct);
        return jobs.Where(j => !j.Active).Select(j => (object)new
        {
            nzo_id       = j.JobId.ToString(),
            name         = DisplayName(j),
            nzb_name     = DisplayName(j),
            category     = "tv",
            status       = j.Status == "Completed" ? "Completed" : "Failed",
            storage      = j.OutputPath ?? "",
            fail_message = j.ErrorMessage ?? "",
            bytes        = j.FileSizeBytes ?? 0,
        }).ToList();
    }

    private static string DisplayName(DownloadJobResponse j) =>
        !string.IsNullOrEmpty(j.EpisodeTitle) ? j.EpisodeTitle
        : !string.IsNullOrEmpty(j.ShowTitle) ? j.ShowTitle
        : j.EpisodeId;

    private static string Megabytes(long? bytes) =>
        ((bytes ?? 0) / 1_000_000.0).ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
}
