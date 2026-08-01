using Krautwatch.Domain.Enums;

namespace Krautwatch.Domain.Entities;

/// <summary>
/// Links one of our broadcaster shows to a TheTVDB series id.
/// </summary>
/// <remarks>
/// <para>
/// This deliberately lives in its own table rather than on <see cref="Show"/>. The crawl upsert marks
/// pre-existing rows <c>EntityState.Modified</c> and writes every column from freshly built crawler
/// entities, so anything stamped onto <c>Show.TvdbId</c> is wiped by the next crawl. Keeping mappings
/// outside the crawl graph makes them durable *by construction* instead of by remembering to preserve a
/// column.
/// </para>
/// <para>
/// The relationship is many-of-ours to one TVDB id, not one-to-one: a single TVDB series is often split
/// across several Mediathek shows (<c>extra 3 · Der Irrsinn der Woche</c> on ARD alongside <c>extra 3</c>
/// and <c>extra 3 Spezial: Der reale Irrsinn</c> on ZDF all belong to tvdb 255986). Hence the composite key.
/// </para>
/// </remarks>
public class ShowMapping
{
    /// <summary>
    /// How many grabs of the same show, for the same TVDB id, before we stop asking and pick it ourselves.
    /// </summary>
    /// <remarks>
    /// A repeated choice is evidence; a single one is not. Newznab cannot tell an interactive search from a
    /// scheduled one, so one grab may have had no human behind it — but five, with alternatives on offer
    /// every time, is a decision. Lives on the entity because both the Indexing resolver and the Settings
    /// read model need it, and DR-009 forbids one slice reaching into another.
    /// </remarks>
    public const int AutoSelectAfterPicks = 5;

    /// <summary>The TheTVDB series id — the identity Sonarr asks us about.</summary>
    public int TvdbId { get; init; }

    /// <summary>Our provider-prefixed show id, e.g. <c>ard:extra-3-der-irrsinn-der-woche</c>.</summary>
    public string ShowId { get; init; } = default!;

    public Show Show { get; set; } = default!;

    /// <summary>How much we trust this mapping, and therefore whether it may be revised. Settable so a
    /// weaker mapping can be upgraded in place when stronger evidence arrives.</summary>
    public MappingProvenance Provenance { get; set; } = MappingProvenance.Auto;

    /// <summary>
    /// Why we believe this — the TVDB name we matched, or "operator override". Kept as free text for the
    /// settings UI: a mapping the operator cannot explain is one they cannot sensibly correct.
    /// </summary>
    public string? Evidence { get; set; }

    /// <summary>
    /// How many times a release from this show has been grabbed in answer to this TVDB id.
    /// </summary>
    /// <remarks>
    /// The disambiguation signal. A grab is a deliberate pick out of the candidates we offered, so repeated
    /// picks are the operator telling us the answer — without them ever visiting a settings page. Counting
    /// rather than trusting the first pick is what makes this safe: Newznab cannot distinguish an interactive
    /// search from a scheduled one, so a single grab may have had no human behind it at all.
    /// </remarks>
    public int PickCount { get; set; }

    public DateTimeOffset? LastPickedAt { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>True when this mapping must not be overwritten by automatic re-derivation.</summary>
    public bool IsPinned => Provenance == MappingProvenance.OperatorConfirmed;
}
