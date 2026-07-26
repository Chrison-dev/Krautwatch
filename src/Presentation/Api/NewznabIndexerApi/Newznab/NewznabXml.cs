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
                new XElement("tv-search", new XAttribute("available", "yes"), new XAttribute("supportedParams", "q,season,ep")),
                new XElement("movie-search", new XAttribute("available", "no"), new XAttribute("supportedParams", "q"))),
            new XElement("categories",
                new XElement("category",
                    new XAttribute("id", NewznabCategory.Tv), new XAttribute("name", "TV"),
                    new XElement("subcat", new XAttribute("id", "5040"), new XAttribute("name", "TV/HD"))),
                new XElement("category",
                    new XAttribute("id", NewznabCategory.Movies), new XAttribute("name", "Movies")))));

    public static string Feed(IReadOnlyList<Release> releases, Func<Release, string> downloadUrl) =>
        Doc(new XElement("rss",
            new XAttribute("version", "2.0"),
            new XAttribute(XNamespace.Xmlns + "newznab", Nz.NamespaceName),
            new XElement("channel",
                new XElement("title", "Krautwatch"),
                new XElement("description", "German public-TV Newznab indexer"),
                releases.Select(r => Item(r, downloadUrl(r))))));

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

        if (r.Season is not null) item.Add(Attr("season", r.Season.Value));
        if (r.Episode is not null) item.Add(Attr("episode", r.Episode.Value));
        return item;
    }

    private static XElement Attr(string name, object value) =>
        new(Nz + "attr", new XAttribute("name", name), new XAttribute("value", value));

    /// <summary>A minimal NZB carrying only the download token (SABnzbd decodes it later).</summary>
    public static string Nzb(string token) =>
        Doc(new XElement(NzbNs + "nzb",
            new XElement(NzbNs + "head",
                new XElement(NzbNs + "meta", new XAttribute("type", NzbToken.MetaType), token))));

    private static string Doc(XElement root) =>
        new XDeclaration("1.0", "UTF-8", null) + Environment.NewLine + root;
}
