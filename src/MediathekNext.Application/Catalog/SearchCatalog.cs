using FluentValidation;
using MediathekNext.Domain.Interfaces;

namespace MediathekNext.Application.Catalog;

// ============================================================
// Query
// ============================================================

public record SearchCatalogQuery(string Query);

// ============================================================
// Response DTO
// ============================================================

public record SearchCatalogResponse(
    string EpisodeId,
    string ShowTitle,
    string EpisodeTitle,
    string ChannelId,
    string ChannelName,
    DateTimeOffset BroadcastDate,
    TimeSpan Duration,
    DateTimeOffset? AvailableUntil,
    IReadOnlyList<StreamDto> Streams);

public record StreamDto(
    string StreamId,
    string Quality,
    string Url,
    string Format);

// ============================================================
// Validator
// ============================================================

public class SearchCatalogQueryValidator : AbstractValidator<SearchCatalogQuery>
{
    public SearchCatalogQueryValidator()
    {
        RuleFor(x => x.Query)
            .NotEmpty().WithMessage("Search query must not be empty.")
            .MinimumLength(2).WithMessage("Search query must be at least 2 characters.")
            .MaximumLength(200).WithMessage("Search query must not exceed 200 characters.");
    }
}

// ============================================================
// Handler
// ============================================================

public class SearchCatalogQueryHandler(IEpisodeRepository episodeRepository)
{
    public async Task<IReadOnlyList<SearchCatalogResponse>> HandleAsync(
        SearchCatalogQuery query,
        CancellationToken ct = default)
    {
        var episodes = await episodeRepository.SearchAsync(query.Query, ct);

        return episodes.Select(e => new SearchCatalogResponse(
            EpisodeId:      e.Id,
            ShowTitle:      e.Show.Title,
            EpisodeTitle:   e.Title,
            ChannelId:      e.Show.Channel.Id,
            ChannelName:    e.Show.Channel.Name,
            BroadcastDate:  e.BroadcastDate,
            Duration:       e.Duration,
            AvailableUntil: e.AvailableUntil,
            Streams: e.Streams.Select(s => new StreamDto(
                StreamId: s.Id,
                Quality:  s.Quality.ToString(),
                Url:      s.Url,
                Format:   s.Format)).ToList()
        )).ToList();
    }
}
