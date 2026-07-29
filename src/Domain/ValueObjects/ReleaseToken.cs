namespace Krautwatch.Domain.ValueObjects;

/// <summary>
/// The opaque token that round-trips a release from the Newznab indexer, through Sonarr, to our SABnzbd
/// endpoint — carrying the episode being downloaded and, when the search was id-driven, the TVDB id it was
/// offered as the answer to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the id travels with the token.</b> A grab is the one moment we learn something we cannot infer:
/// out of the candidates we offered for an ambiguous TVDB id, which show was actually wanted. The episode
/// alone tells us the show, but not the question it was answering — so both halves have to survive the
/// round trip. This is what turns Sonarr's interactive search into our disambiguation UI at no extra cost:
/// the operator was going to grab something anyway.
/// </para>
/// <para>
/// <b>Backward compatible by construction.</b> A token with no suffix parses to just an episode id, which is
/// exactly what earlier releases emitted — so NZBs already sitting in a Sonarr queue keep working.
/// </para>
/// </remarks>
public readonly record struct ReleaseToken(string EpisodeId, int? TvdbId)
{
    /// <summary>
    /// Separator between the episode id and the TVDB suffix. A vertical bar cannot occur in a broadcaster
    /// id: those are provider-prefixed URL paths, CRIDs and base64 fragments.
    /// </summary>
    private const char Separator = '|';

    private const string TvdbPrefix = "tvdb=";

    public string Encode() =>
        TvdbId is null ? EpisodeId : $"{EpisodeId}{Separator}{TvdbPrefix}{TvdbId.Value}";

    /// <summary>
    /// Splits a token back into its parts. An unrecognised suffix is ignored rather than rejected — the
    /// episode id is the part that makes the download work, and failing a download over an unparseable
    /// learning hint would be the wrong trade.
    /// </summary>
    public static ReleaseToken Parse(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return new ReleaseToken(string.Empty, null);

        var separator = token.LastIndexOf(Separator);
        if (separator < 0)
            return new ReleaseToken(token, null);

        var episodeId = token[..separator];
        var suffix = token[(separator + 1)..];

        if (suffix.StartsWith(TvdbPrefix, StringComparison.Ordinal)
            && int.TryParse(suffix[TvdbPrefix.Length..], out var tvdbId)
            && tvdbId > 0)
        {
            return new ReleaseToken(episodeId, tvdbId);
        }

        return new ReleaseToken(token, null);
    }
}
