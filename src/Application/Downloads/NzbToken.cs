using System.Xml.Linq;

namespace Krautwatch.Application.Downloads;

/// <summary>
/// The contract for the synthetic NZB that round-trips a download between our two *arr surfaces:
/// the Newznab indexer emits an NZB whose only payload is an opaque token (the <c>Episode.Id</c>);
/// the SABnzbd client reads that token back out to enqueue the real download. We own both ends, so
/// the "NZB" carries nothing but a single <c>&lt;meta type="krautwatch-token"&gt;</c>.
/// </summary>
public static class NzbToken
{
    public const string MetaType = "krautwatch-token";

    public static string? Read(string nzbXml)
    {
        try { return Read(XDocument.Parse(nzbXml)); }
        catch (System.Xml.XmlException) { return null; }
    }

    public static string? Read(Stream nzb)
    {
        try { return Read(XDocument.Load(nzb)); }
        catch (System.Xml.XmlException) { return null; }
    }

    private static string? Read(XDocument doc) =>
        doc.Descendants()
            .Where(e => e.Name.LocalName == "meta"
                        && (string?)e.Attribute("type") == MetaType)
            .Select(e => e.Value.Trim())
            .FirstOrDefault(v => !string.IsNullOrEmpty(v));
}
