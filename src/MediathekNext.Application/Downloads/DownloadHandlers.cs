using FluentValidation;
using MediathekNext.Domain.Entities;
using MediathekNext.Domain.Enums;
using MediathekNext.Domain.Interfaces;

namespace MediathekNext.Application.Downloads;

// ============================================================
// Request DTO
// ============================================================

public record StartDownloadRequest(string EpisodeId, string StreamId);

public class StartDownloadRequestValidator : AbstractValidator<StartDownloadRequest>
{
    public StartDownloadRequestValidator()
    {
        RuleFor(x => x.EpisodeId).NotEmpty().WithMessage("Episode ID is required.");
        RuleFor(x => x.StreamId).NotEmpty().WithMessage("Stream ID is required.");
    }
}

// ============================================================
// Start download
// ============================================================

public class StartDownloadHandler(
    IEpisodeRepository episodeRepository,
    IDownloadJobRepository jobRepository,
    IDownloadQueue downloadQueue)
{
    public async Task<DownloadJobResponse?> HandleAsync(
        StartDownloadRequest request,
        CancellationToken ct = default)
    {
        var episode = await episodeRepository.GetByIdAsync(request.EpisodeId, ct);
        if (episode is null) return null;

        var stream = episode.Streams.FirstOrDefault(s => s.Id == request.StreamId);
        if (stream is null) return null;

        var job = new DownloadJob
        {
            Id        = Guid.NewGuid(),
            EpisodeId = episode.Id,
            Episode   = episode,
            StreamUrl = stream.Url,
            Quality   = stream.Quality,
        };

        await jobRepository.AddAsync(job, ct);
        await downloadQueue.EnqueueAsync(job.Id, stream.Url, ct);

        return DownloadJobMapper.ToResponse(job);
    }
}

// ============================================================
// Cancel
// ============================================================

public class CancelDownloadHandler(IDownloadJobRepository jobRepository)
{
    public async Task<bool> HandleAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await jobRepository.GetByIdAsync(jobId, ct);
        if (job is null || job.IsTerminal) return false;

        job.MarkCancelled();
        await jobRepository.UpdateAsync(job, ct);
        return true;
    }
}

// ============================================================
// Retry
// ============================================================

public class RetryDownloadHandler(
    IDownloadJobRepository jobRepository,
    IDownloadQueue downloadQueue)
{
    public async Task<DownloadJobResponse?> HandleAsync(Guid jobId, CancellationToken ct = default)
    {
        var original = await jobRepository.GetByIdAsync(jobId, ct);
        if (original is null || !original.IsTerminal || original.Status == DownloadStatus.Completed)
            return null;

        var retryJob = new DownloadJob
        {
            Id        = Guid.NewGuid(),
            EpisodeId = original.EpisodeId,
            Episode   = original.Episode,
            StreamUrl = original.StreamUrl,
            Quality   = original.Quality,
        };

        await jobRepository.AddAsync(retryJob, ct);
        await downloadQueue.RequeueAsync(retryJob.Id, retryJob.StreamUrl, ct);

        return DownloadJobMapper.ToResponse(retryJob);
    }
}

// ============================================================
// Queue / single job queries
// ============================================================

public class GetDownloadQueueHandler(IDownloadJobRepository jobRepository)
{
    public async Task<IReadOnlyList<DownloadJobResponse>> HandleAsync(CancellationToken ct = default)
    {
        var jobs = await jobRepository.GetAllAsync(ct);
        return jobs.Select(DownloadJobMapper.ToResponse).ToList();
    }
}

public class GetDownloadJobHandler(IDownloadJobRepository jobRepository)
{
    public async Task<DownloadJobResponse?> HandleAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await jobRepository.GetByIdAsync(jobId, ct);
        return job is null ? null : DownloadJobMapper.ToResponse(job);
    }
}

// ============================================================
// Shared mapping
// ============================================================

public static class DownloadJobMapper
{
    public static DownloadJobResponse ToResponse(DownloadJob job) => new(
        JobId:           job.Id,
        EpisodeId:       job.EpisodeId,
        EpisodeTitle:    job.Episode?.Title ?? "",
        ShowTitle:       job.Episode?.Show?.Title ?? "",
        ChannelName:     job.Episode?.Show?.Channel?.Name ?? "",
        Quality:         job.Quality.ToString(),
        Status:          job.Status.ToString(),
        StreamType:      job.StreamType,
        ProgressPercent: job.ProgressPercent,
        ErrorMessage:    job.ErrorMessage,
        OutputPath:      job.OutputPath,
        FileSizeBytes:   job.FileSizeBytes,
        CreatedAt:       job.CreatedAt,
        StartedAt:       job.StartedAt,
        CompletedAt:     job.CompletedAt);
}
