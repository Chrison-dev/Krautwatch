using FluentValidation;
using MediathekNext.Application.Downloads;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MediathekNext.Api.Endpoints;

public static class DownloadEndpoints
{
    public static IEndpointRouteBuilder MapDownloadEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/downloads")
            .WithTags("Downloads");

        // POST /api/downloads — start a new download
        group.MapPost("/", StartDownloadAsync)
            .WithName("StartDownload")
            .WithSummary("Queue a new download job for an episode stream.");

        // GET /api/downloads — list all jobs (last 24h)
        group.MapGet("/", GetQueueAsync)
            .WithName("GetDownloadQueue")
            .WithSummary("List all download jobs from the last 24 hours.");

        // GET /api/downloads/{id}
        group.MapGet("/{id:guid}", GetJobAsync)
            .WithName("GetDownloadJob")
            .WithSummary("Get status of a specific download job.");

        // DELETE /api/downloads/{id} — cancel
        group.MapDelete("/{id:guid}", CancelDownloadAsync)
            .WithName("CancelDownload")
            .WithSummary("Cancel a queued or in-progress download.");

        // POST /api/downloads/{id}/retry — retry failed job
        group.MapPost("/{id:guid}/retry", RetryDownloadAsync)
            .WithName("RetryDownload")
            .WithSummary("Retry a failed download job.");

        return app;
    }

    // POST /api/downloads
    private static async Task<Results<Created<DownloadJobResponse>, NotFound, BadRequest<ValidationProblem>>> StartDownloadAsync(
        StartDownloadRequest request,
        StartDownloadHandler handler,
        IValidator<StartDownloadRequest> validator,
        CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return TypedResults.BadRequest(ValidationHelper.ToValidationProblem(validation));

        var result = await handler.HandleAsync(request, ct);
        if (result is null)
            return TypedResults.NotFound();

        return TypedResults.Created($"/api/downloads/{result.JobId}", result);
    }

    // GET /api/downloads
    private static async Task<Ok<IReadOnlyList<DownloadJobResponse>>> GetQueueAsync(
        GetDownloadQueueHandler handler,
        CancellationToken ct)
    {
        var results = await handler.HandleAsync(ct);
        return TypedResults.Ok(results);
    }

    // GET /api/downloads/{id}
    private static async Task<Results<Ok<DownloadJobResponse>, NotFound>> GetJobAsync(
        Guid id,
        GetDownloadJobHandler handler,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(id, ct);
        return result is not null ? TypedResults.Ok(result) : TypedResults.NotFound();
    }

    // DELETE /api/downloads/{id}
    private static async Task<Results<NoContent, NotFound>> CancelDownloadAsync(
        Guid id,
        CancelDownloadHandler handler,
        CancellationToken ct)
    {
        var cancelled = await handler.HandleAsync(id, ct);
        return cancelled ? TypedResults.NoContent() : TypedResults.NotFound();
    }

    // POST /api/downloads/{id}/retry
    private static async Task<Results<Created<DownloadJobResponse>, NotFound, Conflict>> RetryDownloadAsync(
        Guid id,
        RetryDownloadHandler handler,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(id, ct);
        if (result is null)
            return TypedResults.NotFound();

        return TypedResults.Created($"/api/downloads/{result.JobId}", result);
    }
}
