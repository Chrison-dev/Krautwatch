namespace Krautwatch.Domain.Enums;

/// <summary>
/// How a show's episodes are numbered and matched — adopted from Sonarr's series model. The value
/// selects the matching regime the Newznab indexer emits releases for (DR-010):
/// <list type="bullet">
/// <item><b>Standard</b> — season/episode (SxxEyy).</item>
/// <item><b>Daily</b> — air-date (yyyy-MM-dd); the default for dated German public-TV content.</item>
/// <item><b>Anime</b> — absolute episode number.</item>
/// </list>
/// </summary>
public enum SeriesType
{
    Standard = 0,
    Daily    = 1,
    Anime    = 2,
}
