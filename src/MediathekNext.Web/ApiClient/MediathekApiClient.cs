namespace MediathekNext.Web.ApiClient;

// ──────────────────────────────────────────────────────────────
// DTOs (mirror Application layer — no project reference needed)
// ──────────────────────────────────────────────────────────────

public record EpisodeSummary(
    string EpisodeId,
    string ShowTitle,
    string EpisodeTitle,
    string ChannelId,
    string ChannelName,
    string ContentType,
    DateTimeOffset BroadcastDate,
    TimeSpan Duration,
    bool HasStreams);

public record ShowSummary(
    string ShowId,
    string Title,
    string ChannelId,
    string ChannelName,
    int EpisodeCount,
    DateTimeOffset? LatestBroadcast);

public record EpisodeDetail(
    string EpisodeId,
    string ShowTitle,
    string EpisodeTitle,
    string? Description,
    string ChannelId,
    string ChannelName,
    string ContentType,
    DateTimeOffset BroadcastDate,
    TimeSpan Duration,
    List<StreamDto> Streams);

public record StreamDto(
    string StreamId,
    string Quality,
    string Url,
    string Format);

public record DownloadJob(
    Guid JobId,
    string EpisodeId,
    string EpisodeTitle,
    string ShowTitle,
    string ChannelName,
    string Quality,
    string Status,
    string? StreamType,
    double? ProgressPercent,
    string? ErrorMessage,
    string? OutputPath,
    long? FileSizeBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

public record StartDownloadRequest(string EpisodeId, string StreamId);

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
// Typed API client
// ──────────────────────────────────────────────────────────────

public class MediathekApiClient(HttpClient http)
{
    // Catalog
    public Task<List<EpisodeSummary>?> SearchAsync(string q, CancellationToken ct = default) =>
        http.GetFromJsonAsync<List<EpisodeSummary>>($"/api/catalog/search?q={Uri.EscapeDataString(q)}", ct);

    public Task<List<ShowSummary>?> GetShowsAsync(string? channelId = null, CancellationToken ct = default) =>
        http.GetFromJsonAsync<List<ShowSummary>>(
            channelId is null ? "/api/catalog/shows" : $"/api/catalog/shows?channelId={channelId}", ct);

    public Task<List<EpisodeSummary>?> GetShowEpisodesAsync(string showId, CancellationToken ct = default) =>
        http.GetFromJsonAsync<List<EpisodeSummary>>($"/api/catalog/shows/{showId}/episodes", ct);

    public Task<EpisodeDetail?> GetEpisodeDetailAsync(string episodeId, CancellationToken ct = default) =>
        http.GetFromJsonAsync<EpisodeDetail>($"/api/catalog/episodes/{episodeId}", ct);

    public Task<List<EpisodeSummary>?> BrowseByChannelAsync(string channelId, string? contentType = null, CancellationToken ct = default) =>
        http.GetFromJsonAsync<List<EpisodeSummary>>(
            contentType is null
                ? $"/api/catalog/channels/{channelId}"
                : $"/api/catalog/channels/{channelId}?contentType={contentType}", ct);

    public Task<List<EpisodeSummary>?> BrowseByContentTypeAsync(string contentType, string? channelId = null, CancellationToken ct = default) =>
        http.GetFromJsonAsync<List<EpisodeSummary>>(
            channelId is null
                ? $"/api/catalog/type/{contentType}"
                : $"/api/catalog/type/{contentType}?channelId={channelId}", ct);

    // Downloads
    public Task<List<DownloadJob>?> GetDownloadQueueAsync(CancellationToken ct = default) =>
        http.GetFromJsonAsync<List<DownloadJob>>("/api/downloads", ct);

    public async Task<DownloadJob?> StartDownloadAsync(StartDownloadRequest request, CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync("/api/downloads", request, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<DownloadJob>(ct);
    }

    public async Task<bool> CancelDownloadAsync(Guid jobId, CancellationToken ct = default)
    {
        var resp = await http.DeleteAsync($"/api/downloads/{jobId}", ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<DownloadJob?> RetryDownloadAsync(Guid jobId, CancellationToken ct = default)
    {
        var resp = await http.PostAsync($"/api/downloads/{jobId}/retry", null, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<DownloadJob>(ct);
    }
    // Settings
    public Task<SettingsResponse?> GetSettingsAsync(CancellationToken ct = default) =>
        http.GetFromJsonAsync<SettingsResponse>("/api/settings", ct);

    public async Task<SettingsResponse?> SaveSettingsAsync(SaveSettingsRequest request, CancellationToken ct = default)
    {
        var resp = await http.PutAsJsonAsync("/api/settings", request, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<SettingsResponse>(ct);
    }
    // System status
    public Task<SystemStatusResponse?> GetSystemStatusAsync(CancellationToken ct = default) =>
        http.GetFromJsonAsync<SystemStatusResponse>("/api/system/status", ct);
}

public record SystemStatusResponse(
    string State,
    long CatalogEntryCount,
    DateTimeOffset? LastRefreshedAt,
    string? CurrentTask,
    string? ErrorMessage,
    List<SystemStepResponse> Steps);

public record SystemStepResponse(string Name, string Status, string? Detail);
