namespace Krautwatch.Domain.Enums;

/// <summary>
/// Where a show↔TVDB-id mapping came from. This drives whether the mapping may be silently revised:
/// a wrong id is worse than no id at all, because Sonarr trusts the id over the release title and will
/// quietly file episodes under the wrong series. So the weaker the evidence, the more willing we are to
/// replace it later.
/// </summary>
public enum MappingProvenance
{
    /// <summary>
    /// Derived without a human: the TVDB record matched exactly one of our shows unambiguously.
    /// Freely replaceable by better evidence.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Inferred from grabs. We offered several candidates and something picked this one — usually a human in
    /// Sonarr's interactive search, but Newznab cannot distinguish that from a scheduled search, so a single
    /// pick is *probable* consent at best.
    /// </summary>
    /// <remarks>
    /// Which is why picks are counted rather than trusted individually: one stray automatic grab moves a
    /// counter, and only a repeated choice becomes a decision. See
    /// <c>TvdbShowResolver.AutoSelectAfterPicks</c>.
    /// </remarks>
    Learned = 1,

    /// <summary>
    /// Imported from a mapping file — our own export, or a curated third-party set such as RundfunkArr's
    /// <c>rulesets.json</c>. Someone vouched for it, but not this operator and not against this catalog, so
    /// it ranks below an explicit local override.
    /// </summary>
    Imported = 3,

    /// <summary>
    /// Set explicitly by the operator in our own UI. Never revised automatically — an override exists
    /// precisely because the automatic answer was wrong, so re-deriving it would undo the fix.
    /// </summary>
    OperatorConfirmed = 2,
}
