using FluentValidation;
using MediathekNext.Domain.Interfaces;

namespace MediathekNext.Application.Catalog;

// ============================================================
// Query
// ============================================================

public record GetEpisodeDetailQuery(string EpisodeId);

// ============================================================
// Response DTO
// ============================================================

public record EpisodeDetailResponse(
    string EpisodeId,
    string ShowTitle,
    string EpisodeTitle,
    string? Description,
    string ChannelId,
    string ChannelName,
    DateTimeOffset BroadcastDate,
    TimeSpan Duration,
    DateTimeOffset? AvailableUntil,
    IReadOnlyList<StreamDto> Streams);

// ============================================================
// Validator
// ============================================================

public class GetEpisodeDetailQueryValidator : AbstractValidator<GetEpisodeDetailQuery>
{
    public GetEpisodeDetailQueryValidator()
    {
        RuleFor(x => x.EpisodeId)
            .NotEmpty().WithMessage("Episode ID must not be empty.");
    }
}

// ============================================================
// Handler
// ============================================================

public class GetEpisodeDetailQueryHandler(IEpisodeRepository episodeRepository)
{
    public async Task<EpisodeDetailResponse?> HandleAsync(
        GetEpisodeDetailQuery query,
        CancellationToken ct = default)
    {
        var episode = await episodeRepository.GetByIdAsync(query.EpisodeId, ct);

        if (episode is null)
            return null;

        return new EpisodeDetailResponse(
            EpisodeId:      episode.Id,
            ShowTitle:      episode.Show.Title,
            EpisodeTitle:   episode.Title,
            Description:    episode.Description,
            ChannelId:      episode.Show.Channel.Id,
            ChannelName:    episode.Show.Channel.Name,
            BroadcastDate:  episode.BroadcastDate,
            Duration:       episode.Duration,
            AvailableUntil: episode.AvailableUntil,
            Streams: episode.Streams.Select(s => new StreamDto(
                StreamId: s.Id,
                Quality:  s.Quality.ToString(),
                Url:      s.Url,
                Format:   s.Format)).ToList()
        );
    }
}
