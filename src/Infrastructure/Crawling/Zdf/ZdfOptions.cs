namespace Krautwatch.Infrastructure.Crawling.Zdf;

/// <summary>
/// ZDF client settings, bound from the <c>Zdf</c> configuration section (#13).
/// </summary>
public sealed class ZdfOptions
{
    public const string SectionName = "Zdf";

    /// <summary>
    /// The value ZDF's own web player ships with, correct as of 2026-08. Kept as the default so an
    /// upgrade needs no configuration — but it is a default, not a constant, which is the entire point:
    /// when ZDF rotates it, recovery is a config change and a restart rather than a rebuild.
    /// </summary>
    public const string DefaultApiAuthKey = "aa3noh4ohz9eeboo8shiesheec9ciequ9Quah7el";

    /// <summary>
    /// The static bearer sent as <c>Api-Auth</c> on every api.zdf.de request. Override with
    /// <c>Zdf__ApiAuthKey</c> (or <c>Zdf:ApiAuthKey</c>) when ZDF rotates it.
    /// </summary>
    public string ApiAuthKey { get; set; } = DefaultApiAuthKey;
}
