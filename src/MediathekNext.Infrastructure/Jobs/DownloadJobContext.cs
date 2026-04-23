namespace MediathekNext.Infrastructure.Jobs;

/// <summary>
/// Payload serialised into TickerQ's TimeTicker.Request field and passed
/// from phase to phase through the download job chain.
///
/// Each phase reads what it needs and adds its own outputs.
/// Must be JSON-serialisable (TickerQ uses System.Text.Json internally).
/// </summary>
public record DownloadJobContext(
    Guid DownloadJobId,
    string StreamUrl,
    string? StreamType          = null,   // populated by ResolveStreamJob
    long?   ContentLengthBytes  = null,   // populated by ResolveStreamJob
    string? TempPath            = null,   // populated by DownloadStreamJob
    string? FinalPath           = null);  // populated by DownloadStreamJob (target)
