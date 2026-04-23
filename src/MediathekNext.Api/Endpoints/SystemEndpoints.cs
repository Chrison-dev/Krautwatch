using MediathekNext.Infrastructure.System;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MediathekNext.Api.Endpoints;

public static class SystemEndpoints
{
    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/system")
            .WithTags("System");

        group.MapGet("/status", GetStatusAsync)
            .WithName("GetSystemStatus")
            .WithSummary("Returns current application state, catalog status, and init step progress.")
            .AllowAnonymous();

        return app;
    }

    private static Ok<SystemStatusResponse> GetStatusAsync(SystemStatusService statusService)
    {
        var snapshot = statusService.GetSnapshot();

        var response = new SystemStatusResponse(
            State:             snapshot.State.ToString().ToLowerInvariant(),
            CatalogEntryCount: snapshot.CatalogEntryCount,
            LastRefreshedAt:   snapshot.LastRefreshedAt,
            CurrentTask:       snapshot.CurrentTask,
            ErrorMessage:      snapshot.ErrorMessage,
            Steps: snapshot.Steps.Select(s => new SystemStepResponse(
                Name:   s.Name,
                Status: s.Status.ToString().ToLowerInvariant(),
                Detail: s.Detail)).ToList());

        return TypedResults.Ok(response);
    }
}

// ── Response DTOs ──────────────────────────────────────────────

public record SystemStatusResponse(
    string State,               // "initialising" | "ready" | "error"
    long CatalogEntryCount,
    DateTimeOffset? LastRefreshedAt,
    string? CurrentTask,
    string? ErrorMessage,
    IReadOnlyList<SystemStepResponse> Steps);

public record SystemStepResponse(
    string Name,
    string Status,              // "pending" | "in_progress" | "complete" | "failed"
    string? Detail);
