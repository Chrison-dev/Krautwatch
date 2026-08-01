namespace Krautwatch.Domain.Entities;

/// <summary>
/// A curated "this broadcaster show is that TVDB series" pair imported from an external set, held until
/// our catalog actually contains the show.
/// </summary>
/// <remarks>
/// <para>
/// Third-party mapping sets — RundfunkArr's <c>rulesets.json</c> is the one we support — identify the
/// broadcaster side by its <b>Mediathek topic name</b>, not by our show ids, which they have never seen.
/// A name cannot become a <see cref="ShowMapping"/> until we have crawled a show to attach it to, and most
/// of an imported set will name shows this instance has never fetched.
/// </para>
/// <para>
/// So imports land here first and are consumed opportunistically: when a TVDB id is resolved, any hint for
/// it contributes its topic as an extra alias for the series, which lets the ordinary matcher find our show
/// by a name TVDB itself may not carry. The hint never bypasses episode corroboration — a curated pair is
/// good evidence about naming, not proof that this instance's catalog contains that series.
/// </para>
/// </remarks>
public class ImportedShowHint
{
    /// <summary>The TheTVDB series id the source assigned.</summary>
    public int TvdbId { get; init; }

    /// <summary>The broadcaster show name as the source wrote it, normalised for comparison.</summary>
    public string NormalizedTopic { get; init; } = default!;

    /// <summary>The original, unnormalised name — shown in the UI so the operator recognises it.</summary>
    public string Topic { get; init; } = default!;

    /// <summary>Where it came from, e.g. <c>rundfunkarr</c>. Lets one source be re-imported or cleared.</summary>
    public string Source { get; init; } = default!;

    public DateTimeOffset ImportedAt { get; init; } = DateTimeOffset.UtcNow;
}
