using Krautwatch.Domain.Entities;
using Riok.Mapperly.Abstractions;

namespace Krautwatch.Application.Indexing;

/// <summary>
/// Source-generated (Mapperly) projection of a catalog <see cref="Episode"/> into a Newznab
/// <see cref="Release"/>. Copies the raw fields (with a couple of renames/flattens); the release
/// <c>Title</c>, <c>Size</c> and <c>Category</c> are computed on the record itself.
/// </summary>
[Mapper]
public static partial class ReleaseMapper
{
    [MapProperty(nameof(Episode.Id), nameof(Release.Guid))]
    [MapProperty(nameof(Episode.Id), nameof(Release.DownloadToken))]
    [MapProperty([nameof(Episode.Show), nameof(Show.Title)], [nameof(Release.ShowTitle)])]
    [MapProperty([nameof(Episode.Show), nameof(Show.SeriesType)], [nameof(Release.SeriesType)])]
    [MapProperty(nameof(Episode.SeasonNumber), nameof(Release.Season))]
    [MapProperty(nameof(Episode.EpisodeNumber), nameof(Release.Episode))]
    [MapProperty(nameof(Episode.BroadcastDate), nameof(Release.PublishDate))]
    public static partial Release ToRelease(Episode episode);
}
