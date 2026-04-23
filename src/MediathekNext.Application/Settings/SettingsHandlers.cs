using FluentValidation;
using MediathekNext.Domain.Entities;
using MediathekNext.Domain.Interfaces;

namespace MediathekNext.Application.Settings;

// ──────────────────────────────────────────────────────────────
// DTOs
// ──────────────────────────────────────────────────────────────

public record SettingsResponse(
    string DownloadDirectory,
    int MaxConcurrentDownloads,
    int CatalogRefreshIntervalHours,
    string CatalogProviderKey);

public record SaveSettingsRequest(
    string DownloadDirectory,
    int MaxConcurrentDownloads,
    int CatalogRefreshIntervalHours);

// ──────────────────────────────────────────────────────────────
// Validator
// ──────────────────────────────────────────────────────────────

public class SaveSettingsRequestValidator : AbstractValidator<SaveSettingsRequest>
{
    public SaveSettingsRequestValidator()
    {
        RuleFor(x => x.DownloadDirectory)
            .NotEmpty().WithMessage("Download directory must not be empty.")
            .MaximumLength(500).WithMessage("Path must be 500 characters or fewer.");

        RuleFor(x => x.MaxConcurrentDownloads)
            .InclusiveBetween(1, 16)
            .WithMessage("Max concurrent downloads must be between 1 and 16.");

        RuleFor(x => x.CatalogRefreshIntervalHours)
            .InclusiveBetween(1, 168) // 1 hour to 1 week
            .WithMessage("Refresh interval must be between 1 and 168 hours.");
    }
}

// ──────────────────────────────────────────────────────────────
// Handlers
// ──────────────────────────────────────────────────────────────

public class GetSettingsHandler(ISettingsRepository repository)
{
    public async Task<SettingsResponse> HandleAsync(CancellationToken ct = default)
    {
        var settings = await repository.GetAsync(ct);
        return SettingsMapper.ToResponse(settings);
    }
}

public class SaveSettingsHandler(ISettingsRepository repository)
{
    public async Task<SettingsResponse> HandleAsync(
        SaveSettingsRequest request,
        CancellationToken ct = default)
    {
        var settings = await repository.GetAsync(ct);

        settings.DownloadDirectory          = request.DownloadDirectory;
        settings.MaxConcurrentDownloads     = request.MaxConcurrentDownloads;
        settings.CatalogRefreshIntervalHours = request.CatalogRefreshIntervalHours;

        await repository.SaveAsync(settings, ct);
        return SettingsMapper.ToResponse(settings);
    }
}

file static class SettingsMapper
{
    public static SettingsResponse ToResponse(AppSettings s) => new(
        DownloadDirectory:           s.DownloadDirectory,
        MaxConcurrentDownloads:      s.MaxConcurrentDownloads,
        CatalogRefreshIntervalHours: s.CatalogRefreshIntervalHours,
        CatalogProviderKey:          s.CatalogProviderKey);
}
