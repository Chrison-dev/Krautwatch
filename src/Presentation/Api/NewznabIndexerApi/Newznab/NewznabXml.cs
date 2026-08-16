using System.Globalization;
using System.Xml.Linq;
using Krautwatch.Application.Downloads;
using Krautwatch.Application.Indexing;

namespace Krautwatch.Api.NewznabIndexerApi.Newznab;

/// <summary>
/// Renders the Newznab XML documents. RSS 2.0 + the <c>newznab:</c> attribute namespace for search
/// results; a small <c>caps</c> document; and a minimal NZB whose only payload is the opaque token
/// the SABnzbd side (a later increment) decodes back into a download.
/// </summary>
public static class NewznabXml
{
    private static readonly XNamespace Nz = "http://www.newznab.com/DTD/2010/feeds/attributes/";
    private static readonly XNamespace NzbNs = "http://www.newzbin.com/DTD/2003/nzb";

    public static string Capabilities() =>
        Doc(new XElement("caps",
            new XElement("server",
                new XAttribute("version", "1.0"),
                new XAttribute("title", "Krautwatch"),
                new XAttribute("strapline", "German public-TV indexer")),
            new XElement("limits", new XAttribute("max", "500"), new XAttribute("default", "100")),
            new XElement("searching",
                new XElement("search", new XAttribute("available", "yes"), new XAttribute("supportedParams", "q")),
                new XElement("tv-search", new XAttribute("available", "yes"),
                    // Sonarr reads caps and only sends what we advertise, so tvdbid has to be listed
                    // here or it will keep falling back to title-only searches.
                    new XAttribute("supportedParams", "q,tvdbid,season,ep")),
                new XElement("movie-search", new XAttribute("available", "no"), new XAttribute("supportedParams", "q"))),
            new XElement("categories",
                new XElement("category",
                    new XAttribute("id", NewznabCategory.Tv), new XAttribute("name", "TV"),
                    new XElement("subcat", new XAttribute("id", "5040"), new XAttribute("name", "TV/HD"))),
                new XElement("category",
                    new XAttribute("id", NewznabCategory.Movies), new XAttribute("name", "Movies")))));

    /// <summary>
    /// The RSS document Sonarr polls.
    /// </summary>
    /// <remarks>
    /// The <c>newznab:response</c> element is what makes paging work: it tells a client where the page
    /// it just received starts and how many results exist in total, which is how it knows whether to
    /// ask for more and when to stop. Without it a client catching up after downtime has to guess —
    /// and before #12 there was nothing to guess with, because <c>offset</c> was ignored outright and
    /// every page came back as page one.
    /// </remarks>
    public static string Feed(ReleasePage page, Func<Release, string> downloadUrl) =>
        Doc(new XElement("rss",
            new XAttribute("version", "2.0"),
            new XAttribute(XNamespace.Xmlns + "newznab", Nz.NamespaceName),
            new XElement("channel",
                new XElement("title", "Krautwatch"),
                new XElement("description", "German public-TV Newznab indexer"),
                new XElement(Nz + "response",
                    new XAttribute("offset", page.Offset),
                    new XAttribute("total", page.Total)),
                page.Releases.Select(r => Item(r, downloadUrl(r))))));

    private static XElement Item(Release r, string url)
    {
        var item = new XElement("item",
            new XElement("title", r.Title),
            new XElement("guid", new XAttribute("isPermaLink", "false"), r.Guid),
            new XElement("link", url),
            new XElement("pubDate", r.PublishDate.ToString("r", CultureInfo.InvariantCulture)),
            new XElement("enclosure",
                new XAttribute("url", url),
                new XAttribute("length", r.Size),
                new XAttribute("type", "application/x-nzb")),
            Attr("category", r.Category),
            Attr("size", r.Size));

        // The one attribute that lets Sonarr skip title parsing entirely.
        if (r.TvdbId is not null) item.Add(Attr("tvdbid", r.TvdbId.Value));
        if (r.Season is not null) item.Add(Attr("season", r.Season.Value));
        if (r.Episode is not null) item.Add(Attr("episode", r.Episode.Value));
        return item;
    }

    private static XElement Attr(string name, object value) =>
        new(Nz + "attr", new XAttribute("name", name), new XAttribute("value", value));

    /// <summary>
    /// A synthetic NZB carrying our download token. The token in <c>head</c> is the payload we actually
    /// use; the <c>file</c> element below exists solely to satisfy Sonarr.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sonarr runs <c>NzbValidationService</c> over every NZB <b>before</b> handing it to a download
    /// client, and rejects one with no <c>file</c> elements — <c>Invalid NZB: No files</c>. A
    /// token-only NZB therefore fails at the grab, and the download client is never even contacted, which
    /// presents as the indexer being broken rather than the NZB being unusual.
    /// </para>
    /// <para>
    /// So we emit one placeholder file with a single segment. Nothing ever reads it: we own both ends, and
    /// our SABnzbd endpoint pulls the real stream from the token. It is inert padding that makes a
    /// legitimate NZB, and the <c>subject</c> carries the release name so anything inspecting the file by
    /// hand sees something meaningful.
    /// </para>
    /// </remarks>
    public static string Nzb(string token, string? releaseName = null)
    {
        var subject = string.IsNullOrWhiteSpace(releaseName) ? "krautwatch" : releaseName;

        return Doc(new XElement(NzbNs + "nzb",
            new XElement(NzbNs + "head",
                new XElement(NzbNs + "meta", new XAttribute("type", NzbToken.MetaType), token)),
            new XElement(NzbNs + "file",
                new XAttribute("poster", "krautwatch@localhost"),
                new XAttribute("date", DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                new XAttribute("subject", $"\"{subject}.mp4\" yEnc (1/1)"),
                new XElement(NzbNs + "groups",
                    new XElement(NzbNs + "group", "alt.binaries.krautwatch")),
                new XElement(NzbNs + "segments",
                    new XElement(NzbNs + "segment",
                        new XAttribute("bytes", 1),
                        new XAttribute("number", 1),
                        "krautwatch@localhost")))));
    }

    private static string Doc(XElement root) =>
        new XDeclaration("1.0", "UTF-8", null) + Environment.NewLine + root;
}
