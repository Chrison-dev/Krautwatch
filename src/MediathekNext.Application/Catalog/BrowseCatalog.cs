using FluentValidation;
using MediathekNext.Domain.Enums;
using MediathekNext.Domain.Interfaces;

namespace MediathekNext.Application.Catalog;

// ============================================================
// Shared browse response DTOs
// ============================================================

public record ShowSummaryResponse(
    string ShowId,
    string Title,
    string ChannelId,
    string ChannelName,
    int EpisodeCount,
    DateTimeOffset? LatestBroadcast);

public record EpisodeSummaryResponse(
    string EpisodeId,
    string ShowTitle,
    string EpisodeTitle,
    string ChannelId,
    string ChannelName,
    string ContentType,
    DateTimeOffset BroadcastDate,
    TimeSpan Duration,
    bool HasStreams);

// ============================================================
// US-007: Get all shows (optionally by channel)
// ============================================================

public record GetShowsQuery(string? ChannelId = null);

public class GetShowsQueryHandler(IEpisodeRepository repository)
{
    public async Task<IReadOnlyList<ShowSummaryResponse>> HandleAsync(
        GetShowsQuery query, CancellationToken ct = default)
    {
        var shows = await repository.GetShowsAsync(query.ChannelId, ct);
        return shows.Select(x => new ShowSummaryResponse(
            ShowId:          x.Show.Id,
            Title:           x.Show.Title,
            ChannelId:       x.Show.Channel.Id,
            ChannelName:     x.Show.Channel.Name,
            EpisodeCount:    x.EpisodeCount,
            LatestBroadcast: x.LatestBroadcast
        )).ToList();
    }

}

// ============================================================
// US-007: Get all episodes of a specific show
// ============================================================

public record GetShowEpisodesQuery(string ShowId);

public class GetShowEpisodesQueryValidator : AbstractValidator<GetShowEpisodesQuery>
{
    public GetShowEpisodesQueryValidator()
    {
        RuleFor(x => x.ShowId).NotEmpty().WithMessage("Show ID must not be empty.");
    }
}

public class GetShowEpisodesQueryHandler(IEpisodeRepository repository)
{
    public async Task<IReadOnlyList<EpisodeSummaryResponse>> HandleAsync(
        GetShowEpisodesQuery query, CancellationToken ct = default)
    {
        var episodes = await repository.GetByShowAsync(query.ShowId, ct);
        return episodes.Select(BrowseCatalogMapper.ToSummary).ToList();
    }
}

// ============================================================
// Browse by channel (optionally filtered by ContentType)
// ============================================================

public record BrowseByChannelQuery(string ChannelId, string? ContentType = null);

public class BrowseByChannelQueryValidator : AbstractValidator<BrowseByChannelQuery>
{
    public BrowseByChannelQueryValidator()
    {
        RuleFor(x => x.ChannelId).NotEmpty().WithMessage("Channel ID must not be empty.");
        RuleFor(x => x.ContentType)
            .Must(ct => ct is null || Enum.TryParse<ContentType>(ct, true, out _))
            .WithMessage("Invalid content type. Valid values: Episode, Movie, Documentary.");
    }
}

public class BrowseByChannelQueryHandler(IEpisodeRepository repository)
{
    public async Task<IReadOnlyList<EpisodeSummaryResponse>> HandleAsync(
        BrowseByChannelQuery query, CancellationToken ct = default)
    {
        ContentType? contentType = query.ContentType is not null
            ? Enum.Parse<ContentType>(query.ContentType, ignoreCase: true)
            : null;
        var episodes = await repository.GetByChannelAsync(query.ChannelId, contentType, ct);
        return episodes.Select(BrowseCatalogMapper.ToSummary).ToList();
    }
}

// ============================================================
// US-008: Browse by content type (optionally filtered by channel)
// ============================================================

public record BrowseByContentTypeQuery(string ContentType, string? ChannelId = null);

public class BrowseByContentTypeQueryValidator : AbstractValidator<BrowseByContentTypeQuery>
{
    public BrowseByContentTypeQueryValidator()
    {
        RuleFor(x => x.ContentType)
            .NotEmpty()
            .Must(ct => Enum.TryParse<ContentType>(ct, true, out _))
            .WithMessage("Invalid content type. Valid values: Episode, Movie, Documentary.");
    }
}

public class BrowseByContentTypeQueryHandler(IEpisodeRepository repository)
{
    public async Task<IReadOnlyList<EpisodeSummaryResponse>> HandleAsync(
        BrowseByContentTypeQuery query, CancellationToken ct = default)
    {
        var contentType = Enum.Parse<ContentType>(query.ContentType, ignoreCase: true);
        var episodes = await repository.GetByContentTypeAsync(contentType, query.ChannelId, ct);
        return episodes.Select(BrowseCatalogMapper.ToSummary).ToList();
    }
}

// ============================================================
// Shared mapping — single static method, no convoluted extension tricks
// ============================================================

internal static class BrowseCatalogMapper
{
    public static EpisodeSummaryResponse ToSummary(Domain.Entities.Episode e) => new(
        EpisodeId:    e.Id,
        ShowTitle:    e.Show.Title,
        EpisodeTitle: e.Title,
        ChannelId:    e.Show.Channel.Id,
        ChannelName:  e.Show.Channel.Name,
        ContentType:  e.ContentType.ToString(),
        BroadcastDate: e.BroadcastDate,
        Duration:     e.Duration,
        HasStreams:    e.Streams.Count > 0);
}
