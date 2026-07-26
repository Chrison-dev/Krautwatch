namespace Krautwatch.Infrastructure.Crawling;

/// <summary>
/// The normalized program data for a single fetched episode — the contract the broadcaster
/// clients produce and (eventually) the Application/Crawling slices map into the domain model.
/// </summary>
public sealed record EpisodeDetail(
    string Title,
    string Show,
    string Broadcaster,
    DateTimeOffset? AirDate,
    TimeSpan Duration,
    string? Synopsis,
    string? StreamUrl,     // progressive MP4 (preferred)
    string? SubtitleUrl);  // webvtt, if available
