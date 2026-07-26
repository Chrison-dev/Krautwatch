using Krautwatch.Domain.Enums;

namespace Krautwatch.Domain.Entities;

public class Show
{
    public string Id { get; init; } = default!;
    public string Title { get; init; } = default!;
    public string ChannelId { get; init; } = default!;
    public Channel Channel { get; set; } = default!;

    /// <summary>
    /// The numbering/matching regime (Sonarr model). Settable so a crawler can upgrade a show to
    /// <see cref="SeriesType.Standard"/> once it sees episodes carrying SxxEyy; the fallback for
    /// dated Mediathek content is <see cref="SeriesType.Daily"/>.
    /// </summary>
    public SeriesType SeriesType { get; set; } = SeriesType.Daily;

    /// <summary>Optional TheTVDB id — reserved for future TVDB matching; unset until then.</summary>
    public int? TvdbId { get; set; }

    public ICollection<Episode> Episodes { get; init; } = new List<Episode>();
}
