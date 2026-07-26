using System.Text;
using System.Text.Json;

namespace Krautwatch.Infrastructure.Crawling.Ard;

/// <summary>A show resolved from an ARD-platform A-Z catalog.</summary>
public sealed record ArdShow(string Title, string PageId, string PageHref, string? PublicationService);

/// <summary>A full-length episode listed on a show page.</summary>
public sealed record ArdEpisode(
    string Id,
    string Title,
    string ShowTitle,
    DateTimeOffset? BroadcastedOn,
    TimeSpan Duration,
    string ItemHref);

/// <summary>
/// Reads the ARD Mediathek page-gateway. ARD's <c>search-system</c> returns sparse refs that don't
/// resolve, so shows are found the way the site does it: the A-Z catalog (letter widget id is
/// <c>base64("&lt;Brand&gt;.&lt;letter&gt;")</c>) → filter by title → the editorial show page →
/// the "Ganze Folgen" gridlist. KiKA runs on the same platform under its own catalog scope
/// (client <c>kika</c>, brand <c>KiKA</c>) — see DR-010 / issue #10.
/// </summary>
public sealed class ArdCatalogClient(HttpClient http)
{
    public const string ApiBase = "https://api.ardmediathek.de/page-gateway";

    private static (string Path, string Brand) Scope(string client) =>
        client.ToLowerInvariant() switch
        {
            "kika" => ("kika", "KiKA"),
            _      => ("ard",  "ARD"),
        };

    /// <summary>
    /// Find a show by (case-insensitive substring of) its title in the given catalog scope
    /// (<c>ard</c> for regular ARD, <c>kika</c> for KiKA).
    /// </summary>
    public async Task<ArdShow?> FindShowAsync(string query, string client = "ard", CancellationToken ct = default)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0) return null;

        var (path, brand) = Scope(client);
        var letter = char.ToLowerInvariant(trimmed[0]);
        var letterId = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{brand}.{letter}")).TrimEnd('=');

        using var doc = await GetJsonAsync($"{ApiBase}/widgets/{path}/editorials/{letterId}?pageNumber=0&pageSize=200", ct);
        if (doc is null || !doc.RootElement.TryGetProperty("teasers", out var teasers)) return null;

        foreach (var teaser in teasers.EnumerateArray())
        {
            var title = TitleOf(teaser);
            if (title is null || !title.Contains(trimmed, StringComparison.OrdinalIgnoreCase)) continue;

            if (!teaser.TryGetProperty("links", out var links) ||
                !links.TryGetProperty("target", out var target)) continue;

            var href = target.GetProperty("href").GetString();
            var id = target.GetProperty("id").GetString();
            if (href is null || id is null) continue;

            var pubService = teaser.TryGetProperty("publicationService", out var ps) && ps.TryGetProperty("name", out var psn)
                ? psn.GetString() : null;

            return new ArdShow(title, id, href, pubService);
        }
        return null;
    }

    /// <summary>
    /// Fetch a show's episodes: the "Ganze Folgen" (full episodes) gridlist if present,
    /// otherwise every gridlist teaser on the page.
    /// </summary>
    public async Task<IReadOnlyList<ArdEpisode>> GetFullEpisodesAsync(ArdShow show, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(show.PageHref, ct);
        if (doc is null || !doc.RootElement.TryGetProperty("widgets", out var widgets))
            return [];

        var preferred = new List<ArdEpisode>();
        var fallback = new List<ArdEpisode>();

        foreach (var widget in widgets.EnumerateArray())
        {
            if (!widget.TryGetProperty("teasers", out var teasers) || teasers.ValueKind != JsonValueKind.Array) continue;
            var isFullEpisodes = widget.TryGetProperty("title", out var wt) && wt.GetString() == "Ganze Folgen";

            foreach (var teaser in teasers.EnumerateArray())
            {
                var ep = ToEpisode(teaser, show.Title);
                if (ep is null) continue;
                (isFullEpisodes ? preferred : fallback).Add(ep);
            }
        }

        return preferred.Count > 0 ? preferred : fallback;
    }

    private static ArdEpisode? ToEpisode(JsonElement teaser, string showTitle)
    {
        if (!teaser.TryGetProperty("links", out var links) ||
            !links.TryGetProperty("target", out var target)) return null;
        var href = target.TryGetProperty("href", out var h) ? h.GetString() : null;
        var id = teaser.TryGetProperty("id", out var i) ? i.GetString() : null;
        if (href is null || id is null) return null;

        var title = TitleOf(teaser) ?? "";
        var duration = teaser.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
            ? TimeSpan.FromSeconds(d.GetInt32()) : TimeSpan.Zero;
        DateTimeOffset? broadcast = teaser.TryGetProperty("broadcastedOn", out var b) && b.ValueKind == JsonValueKind.String
            ? DateTimeOffset.Parse(b.GetString()!) : null;

        return new ArdEpisode(id, title, showTitle, broadcast, duration, href);
    }

    private static string? TitleOf(JsonElement teaser)
    {
        foreach (var key in new[] { "longTitle", "mediumTitle", "shortTitle", "title" })
            if (teaser.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        return null;
    }

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var resp = await http.GetAsync(url, ct);
            if (resp.IsSuccessStatusCode)
            {
                var stream = await resp.Content.ReadAsStreamAsync(ct);
                return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            }
            if (attempt >= 3) return null;
            await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), ct); // transient backoff
        }
    }
}
