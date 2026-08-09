using Krautwatch.Api.NewznabIndexerApi.Auth;
using Krautwatch.Api.NewznabIndexerApi.Newznab;
using Krautwatch.Application.Indexing;

namespace Krautwatch.Api.NewznabIndexerApi.Endpoints;

/// <summary>
/// The Newznab surface Sonarr/Prowlarr call: <c>/api?t=caps|search|tvsearch</c> and the
/// <c>/download</c> for the synthetic NZB. An apikey is enforced (on search/download) only when one
/// is configured under <c>Newznab:ApiKey</c>; <c>caps</c> stays open so Prowlarr can probe it.
/// </summary>
public static class NewznabEndpoints
{
    public static IEndpointRouteBuilder MapNewznabEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api", HandleApiAsync).WithName("Newznab");
        app.MapGet("/download", HandleDownload).WithName("NewznabDownload");
        return app;
    }

    private static async Task<IResult> HandleApiAsync(
        HttpContext http,
        SearchReleasesHandler search,
        IConfiguration config,
        string? t,
        string? q,
        string? season,
        // Bound as strings, not ints: Sonarr's daily form is `season=2026&ep=06/05`, and an `int` binding
        // made every daily search a 400 before the request ever reached us (#95).
        string? ep,
        int? tvdbid,
        string? apikey,
        int? limit,
        CancellationToken ct)
    {
        switch (t?.ToLowerInvariant())
        {
            case "caps":
                return Xml(NewznabXml.Capabilities());

            case "search":
            case "tvsearch":
                if (!ApiKeyGuard.IsAuthorized(config, apikey)) return Denied();

                var releases = await search.HandleAsync(
                    ToQuery(q, season, ep, limit, tvdbid), ct);

                var baseUrl = $"{http.Request.Scheme}://{http.Request.Host}";
                var key = ApiKeyGuard.Configured(config);
                return Xml(NewznabXml.Feed(releases, r =>
                {
                    // The release name rides along so the NZB, its filename and the file the download
                    // client writes all carry it — that name is what Sonarr's importer parses.
                    var url = $"{baseUrl}/download?token={Uri.EscapeDataString(r.DownloadToken)}"
                            + $"&name={Uri.EscapeDataString(r.Title)}";
                    return key is null ? url : $"{url}&apikey={Uri.EscapeDataString(key)}";
                }));

            default:
                return Results.BadRequest($"Unsupported or missing 't' function: '{t}'.");
        }
    }

    /// <remarks>
    /// <paramref name="name"/> is the release title, echoed into the NZB and its filename. Sonarr names the
    /// downloaded file after the NZB, so this is what ends up on disk and what its importer parses.
    /// </remarks>
    private static IResult HandleDownload(
        HttpContext http,
        IConfiguration config,
        string? token,
        string? apikey,
        string? name)
    {
        if (!ApiKeyGuard.IsAuthorized(config, apikey)) return Denied();
        if (string.IsNullOrWhiteSpace(token)) return Results.BadRequest("Missing 'token'.");

        var releaseName = string.IsNullOrWhiteSpace(name) ? "krautwatch" : SanitiseFileName(name);

        http.Response.Headers.ContentDisposition = $"attachment; filename=\"{releaseName}.nzb\"";
        return Results.Content(NewznabXml.Nzb(token, releaseName), "application/x-nzb");
    }

    /// <summary>Strips anything that cannot appear in a filename or an HTTP header value.</summary>
    private static string SanitiseFileName(string name)
    {
        var cleaned = new string(name.Where(c =>
            !Path.GetInvalidFileNameChars().Contains(c) && c is not ('"' or '\\' or '\r' or '\n')).ToArray());

        return cleaned.Trim() is { Length: > 0 } trimmed ? trimmed : "krautwatch";
    }

    private static IResult Denied() =>
        Results.Json(new { error = new { code = 100, description = "Incorrect or missing API key." } }, statusCode: 401);

    private static IResult Xml(string xml) => Results.Content(xml, "application/xml");

    /// <summary>
    /// Builds the search query, reading the standard/daily/season regime off the request shape rather
    /// than looking the show up — see <see cref="NewznabEpisodeQuery"/>.
    /// </summary>
    private static SearchReleasesQuery ToQuery(
        string? q, string? season, string? ep, int? limit, int? tvdbid)
    {
        var parsed = NewznabEpisodeQuery.Parse(season, ep);

        return new SearchReleasesQuery(
            Q: q,
            Season: parsed.Season,
            Episode: parsed.Episode,
            Limit: limit ?? 100,
            TvdbId: tvdbid,
            AirDate: parsed.AirDate,
            SeasonOnly: parsed.IsSeasonOnly);
    }
}
