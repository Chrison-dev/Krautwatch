namespace Krautwatch.Api.NewznabIndexerApi.Auth;

/// <summary>
/// One instance API key (<c>Krautwatch:ApiKey</c>) shared by both surfaces — the Newznab indexer
/// and the SABnzbd client. When no key is configured everything is open (dev); when one is set it
/// must match. Newznab <c>caps</c> stays open regardless so Prowlarr can probe the indexer.
/// </summary>
public static class ApiKeyGuard
{
    public static string? Configured(IConfiguration config)
    {
        var key = config["Krautwatch:ApiKey"];
        return string.IsNullOrWhiteSpace(key) ? null : key;
    }

    public static bool IsAuthorized(IConfiguration config, string? provided)
    {
        var key = Configured(config);
        return key is null || string.Equals(key, provided, StringComparison.Ordinal);
    }
}
