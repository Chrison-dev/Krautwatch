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
    /// Inferred from a grab. We offered several candidates and something picked one — usually a human in
    /// Sonarr's interactive search, but Newznab cannot distinguish that from a scheduled search, so this
    /// is *probable* consent, not confirmed consent. Replaceable, and surfaced in the UI for correction.
    /// </summary>
    Learned = 1,

    /// <summary>
    /// Set explicitly by the operator in our own UI. Never revised automatically — an override exists
    /// precisely because the automatic answer was wrong, so re-deriving it would undo the fix.
    /// </summary>
    OperatorConfirmed = 2,
}
