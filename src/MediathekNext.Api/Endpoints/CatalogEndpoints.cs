using FluentValidation;
using MediathekNext.Application.Catalog;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MediathekNext.Api.Endpoints;

public static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/catalog")
            .WithTags("Catalog");

        // Search
        group.MapGet("/search", SearchAsync)
            .WithName("SearchCatalog")
            .WithSummary("Full-text search across title, description and show name.");

        // Episode detail
        group.MapGet("/episodes/{episodeId}", GetEpisodeDetailAsync)
            .WithName("GetEpisodeDetail")
            .WithSummary("Get full details for a specific episode.");

        // Shows
        group.MapGet("/shows", GetShowsAsync)
            .WithName("GetShows")
            .WithSummary("List all shows, optionally filtered by channel.");

        group.MapGet("/shows/{showId}/episodes", GetShowEpisodesAsync)
            .WithName("GetShowEpisodes")
            .WithSummary("Get all episodes of a specific show.");

        // Browse by channel
        group.MapGet("/channels/{channelId}", BrowseByChannelAsync)
            .WithName("BrowseByChannel")
            .WithSummary("Browse episodes for a channel, optionally filtered by content type.");

        // Browse by content type
        group.MapGet("/type/{contentType}", BrowseByContentTypeAsync)
            .WithName("BrowseByContentType")
            .WithSummary("Browse by content type (Episode, Movie, Documentary). Optionally filter by channel.");

        return app;
    }

    // GET /api/catalog/search?q=tagesschau
    private static async Task<Results<Ok<IReadOnlyList<SearchCatalogResponse>>, BadRequest<ValidationProblem>>> SearchAsync(
        string q,
        SearchCatalogQueryHandler handler,
        IValidator<SearchCatalogQuery> validator,
        CancellationToken ct)
    {
        var query = new SearchCatalogQuery(q);
        var validation = await validator.ValidateAsync(query, ct);

        if (!validation.IsValid)
            return TypedResults.BadRequest(ValidationHelper.ToValidationProblem(validation));

        var results = await handler.HandleAsync(query, ct);
        return TypedResults.Ok(results);
    }

    // GET /api/catalog/episodes/{episodeId}
    private static async Task<Results<Ok<EpisodeDetailResponse>, NotFound>> GetEpisodeDetailAsync(
        string episodeId,
        GetEpisodeDetailQueryHandler handler,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new GetEpisodeDetailQuery(episodeId), ct);
        return result is not null ? TypedResults.Ok(result) : TypedResults.NotFound();
    }

    // GET /api/catalog/shows?channelId=ard
    private static async Task<Ok<IReadOnlyList<ShowSummaryResponse>>> GetShowsAsync(
        GetShowsQueryHandler handler,
        string? channelId,
        CancellationToken ct)
    {
        var results = await handler.HandleAsync(new GetShowsQuery(channelId), ct);
        return TypedResults.Ok(results);
    }

    // GET /api/catalog/shows/{showId}/episodes
    private static async Task<Results<Ok<IReadOnlyList<EpisodeSummaryResponse>>, BadRequest<ValidationProblem>>> GetShowEpisodesAsync(
        string showId,
        GetShowEpisodesQueryHandler handler,
        IValidator<GetShowEpisodesQuery> validator,
        CancellationToken ct)
    {
        var query = new GetShowEpisodesQuery(showId);
        var validation = await validator.ValidateAsync(query, ct);

        if (!validation.IsValid)
            return TypedResults.BadRequest(ValidationHelper.ToValidationProblem(validation));

        var results = await handler.HandleAsync(query, ct);
        return TypedResults.Ok(results);
    }

    // GET /api/catalog/channels/{channelId}?contentType=Movie
    private static async Task<Results<Ok<IReadOnlyList<EpisodeSummaryResponse>>, BadRequest<ValidationProblem>>> BrowseByChannelAsync(
        string channelId,
        BrowseByChannelQueryHandler handler,
        IValidator<BrowseByChannelQuery> validator,
        string? contentType,
        CancellationToken ct)
    {
        var query = new BrowseByChannelQuery(channelId, contentType);
        var validation = await validator.ValidateAsync(query, ct);

        if (!validation.IsValid)
            return TypedResults.BadRequest(ValidationHelper.ToValidationProblem(validation));

        var results = await handler.HandleAsync(query, ct);
        return TypedResults.Ok(results);
    }

    // GET /api/catalog/type/Movie?channelId=ard
    private static async Task<Results<Ok<IReadOnlyList<EpisodeSummaryResponse>>, BadRequest<ValidationProblem>>> BrowseByContentTypeAsync(
        string contentType,
        BrowseByContentTypeQueryHandler handler,
        IValidator<BrowseByContentTypeQuery> validator,
        string? channelId,
        CancellationToken ct)
    {
        var query = new BrowseByContentTypeQuery(contentType, channelId);
        var validation = await validator.ValidateAsync(query, ct);

        if (!validation.IsValid)
            return TypedResults.BadRequest(ValidationHelper.ToValidationProblem(validation));

        var results = await handler.HandleAsync(query, ct);
        return TypedResults.Ok(results);
    }

    // --------------------------------------------------------
    // Shared helpers
    // --------------------------------------------------------
}

