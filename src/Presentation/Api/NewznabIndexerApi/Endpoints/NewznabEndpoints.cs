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
        int? season,
        int? ep,
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
                    new SearchReleasesQuery(q, season, ep, limit ?? 100, tvdbid), ct);

                var baseUrl = $"{http.Request.Scheme}://{http.Request.Host}";
                var key = ApiKeyGuard.Configured(config);
                return Xml(NewznabXml.Feed(releases, r =>
                {
                    var url = $"{baseUrl}/download?token={Uri.EscapeDataString(r.DownloadToken)}";
                    return key is null ? url : $"{url}&apikey={Uri.EscapeDataString(key)}";
                }));

            default:
                return Results.BadRequest($"Unsupported or missing 't' function: '{t}'.");
        }
    }

    private static IResult HandleDownload(HttpContext http, IConfiguration config, string? token, string? apikey)
    {
        if (!ApiKeyGuard.IsAuthorized(config, apikey)) return Denied();
        if (string.IsNullOrWhiteSpace(token)) return Results.BadRequest("Missing 'token'.");

        http.Response.Headers.ContentDisposition = "attachment; filename=\"krautwatch.nzb\"";
        return Results.Content(NewznabXml.Nzb(token), "application/x-nzb");
    }

    private static IResult Denied() =>
        Results.Json(new { error = new { code = 100, description = "Incorrect or missing API key." } }, statusCode: 401);

    private static IResult Xml(string xml) => Results.Content(xml, "application/xml");
}
