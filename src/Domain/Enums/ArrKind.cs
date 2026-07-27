namespace Krautwatch.Domain.Enums;

/// <summary>
/// Which `*arr` application an instance is, since the two drive different content and expose
/// different API shapes (Sonarr: series; Radarr: movies).
/// </summary>
/// <remarks>
/// Prowlarr is deliberately absent: it is configured *pointing at* Krautwatch as an indexer, so there
/// is nothing for us to call outbound and no instance record to keep (DR-010).
/// </remarks>
public enum ArrKind
{
    Sonarr = 0,
    Radarr = 1,
}
