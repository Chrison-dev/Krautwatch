using FluentValidation;
using MediathekNext.Application.Settings;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MediathekNext.Api.Endpoints;

public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settings")
            .WithTags("Settings");

        // GET /api/settings
        group.MapGet("/", GetAsync)
            .WithName("GetSettings")
            .WithSummary("Retrieve current application settings.");

        // PUT /api/settings
        group.MapPut("/", SaveAsync)
            .WithName("SaveSettings")
            .WithSummary("Update application settings.");

        return app;
    }

    private static async Task<Ok<SettingsResponse>> GetAsync(
        GetSettingsHandler handler,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(ct);
        return TypedResults.Ok(result);
    }

    private static async Task<Results<Ok<SettingsResponse>, BadRequest<ValidationProblem>>> SaveAsync(
        SaveSettingsRequest request,
        SaveSettingsHandler handler,
        IValidator<SaveSettingsRequest> validator,
        CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return TypedResults.BadRequest(ValidationHelper.ToValidationProblem(validation));

        var result = await handler.HandleAsync(request, ct);
        return TypedResults.Ok(result);
    }
}
