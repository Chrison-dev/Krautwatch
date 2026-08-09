using FluentValidation;
using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Enums;
using Krautwatch.Domain.Interfaces;

namespace Krautwatch.Application.Settings;

// ──────────────────────────────────────────────────────────────
// DTOs
// ──────────────────────────────────────────────────────────────

public record SettingsResponse(
    string DownloadDirectory,
    int MaxConcurrentDownloads,
    int CatalogRefreshIntervalHours,
    SearchWaitMode SearchWaitMode,
    int SearchWaitSeconds,
    string TvdbApiKeyMasked,
    bool TvdbKeyFromConfiguration);

public record SaveSettingsRequest(
    string DownloadDirectory,
    int MaxConcurrentDownloads,
    int CatalogRefreshIntervalHours,
    SearchWaitMode SearchWaitMode = SearchWaitMode.ReturnFast,
    int SearchWaitSeconds = 8,
    /// <summary>Blank means "leave unchanged"; the UI never receives the real key back to echo.</summary>
    string? TvdbApiKey = null);

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

        // Only meaningful in ReturnFast mode, but validated regardless so a stale value cannot become
        // active later by flipping the mode back.
        RuleFor(x => x.SearchWaitSeconds)
            .InclusiveBetween(1, 300)
            .WithMessage("Search wait must be between 1 and 300 seconds.");

        RuleFor(x => x.TvdbApiKey)
            .MaximumLength(200).WithMessage("TVDB API key must be 200 characters or fewer.");
    }
}

// ──────────────────────────────────────────────────────────────
// Handlers
// ──────────────────────────────────────────────────────────────

/// <remarks>
/// <c>tvdb</c> is optional so a host that does no TVDB matching needs no adapter wired in; without it the
/// page simply reports TVDB as unconfigured.
/// </remarks>
public class GetSettingsHandler(ISettingsRepository repository, ITvdbCatalog? tvdb = null)
{
    public async Task<SettingsResponse> HandleAsync(CancellationToken ct = default)
    {
        var settings = await repository.GetAsync(ct);
        return SettingsMapper.ToResponse(settings, tvdb);
    }
}

public class SaveSettingsHandler(ISettingsRepository repository, ITvdbCatalog? tvdb = null)
{
    public async Task<SettingsResponse> HandleAsync(
        SaveSettingsRequest request,
        CancellationToken ct = default)
    {
        var settings = await repository.GetAsync(ct);

        settings.DownloadDirectory          = request.DownloadDirectory;
        settings.MaxConcurrentDownloads     = request.MaxConcurrentDownloads;
        settings.CatalogRefreshIntervalHours = request.CatalogRefreshIntervalHours;
        settings.SearchWaitMode             = request.SearchWaitMode;
        settings.SearchWaitSeconds          = request.SearchWaitSeconds;

        // Blank means unchanged — the read model only ever exposes a masked key, so the UI cannot echo the
        // real one back and a blank field must not wipe a configured credential.
        if (!string.IsNullOrWhiteSpace(request.TvdbApiKey))
            settings.TvdbApiKey = request.TvdbApiKey.Trim();

        await repository.SaveAsync(settings, ct);
        return SettingsMapper.ToResponse(settings, tvdb);
    }
}

file static class SettingsMapper
{
    public static SettingsResponse ToResponse(AppSettings s, ITvdbCatalog? tvdb) => new(
        DownloadDirectory:           s.DownloadDirectory,
        MaxConcurrentDownloads:      s.MaxConcurrentDownloads,
        CatalogRefreshIntervalHours: s.CatalogRefreshIntervalHours,
        SearchWaitMode:              s.SearchWaitMode,
        SearchWaitSeconds:           s.SearchWaitSeconds,
        // Never the real key. When configuration supplies it we do not even have the value here — say so
        // rather than masking an empty string and implying none is set.
        TvdbApiKeyMasked:            tvdb?.IsKeyFromConfiguration == true
                                         ? "set by configuration"
                                         : ArrInstanceMapper.Mask(s.TvdbApiKey),
        TvdbKeyFromConfiguration:    tvdb?.IsKeyFromConfiguration ?? false);
}
