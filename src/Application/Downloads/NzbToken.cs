using System.Text.RegularExpressions;
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

    /// <summary>
    /// The release name carried in the NZB's file subject, or null.
    /// </summary>
    /// <remarks>
    /// Sonarr uploads the NZB as multipart form data and does not repeat the release title anywhere in the
    /// query string, so the NZB itself is where we get it back. We wrote the subject in the usual Usenet
    /// shape — <c>"name.ext" yEnc (1/1)</c> — and this pulls the quoted portion back out.
    /// </remarks>
    public static string? ReadReleaseName(Stream nzb)
    {
        try
        {
            var subject = XDocument.Load(nzb).Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "file")
                ?.Attribute("subject")?.Value;

            if (string.IsNullOrWhiteSpace(subject))
                return null;

            var quoted = Regex.Match(subject, "\"(?<name>[^\"]+)\"");
            var name = quoted.Success ? quoted.Groups["name"].Value : subject;

            // Drop the container extension we appended; the release name itself carries no extension.
            name = Regex.Replace(name.Trim(), @"\.(mp4|mkv|ts)$", string.Empty, RegexOptions.IgnoreCase);
            return name.Length > 0 ? name : null;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }
}
