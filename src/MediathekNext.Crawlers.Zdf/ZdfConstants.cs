namespace MediathekNext.Crawlers.Zdf;

internal static class ZdfConstants
{
    // Update AuthKey when ZDF rotates the token and requests start returning 401.
    public const string AuthKey    = "aa3noh4ohz9eeboo8shiesheec9ciequ9Quah7el";
    public const string AuthHeader = "Api-Auth";
    public const string AppId      = "ffw-mt-web-879d5c17";

    public const string ApiBase = "https://api.zdf.de";
    public const string WebBase = "https://www.zdf.de";

    public const int    LetterPageCount  = 27;  // tabs 0-26 (A–Z + special)
    public const int    EpisodesPageSize = 24;

    // Persisted GraphQL query hashes — update if ZDF deploys a new API version.
    public const string HashLetterPage        = "63848395d2f977dbf99ce30172c8d80038a54615574295eee6f8704c5e6fcbee";
    public const string HashTopicSeason       = "9412a0f4ac55dc37d46975d461ec64bfd14380d815df843a1492348f77b5c99a";
    public const string HashSpecialCollection = "c85ca9c636258a65961a81124abd0dbef06ab97eaca9345cbdfde23b54117242";

    public static readonly Dictionary<string, string> SpecialCollections = new()
    {
        ["pub-form-10004"] = "Filme",
        ["pub-form-10003"] = "Dokus",
        ["pub-form-10010"] = "Serien",
        ["genre-10290"]    = "Sport",
    };

    public static readonly Dictionary<string, string> PartnerToKey =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ZDFinfo"] = "ZDFinfo",
            ["ZDFneo"]  = "ZDFneo",
            ["ZDF"]     = "ZDF",
            ["EMPTY"]   = "ZDF",
            ["ZDFtivi"] = "ZDFtivi",
        };

    public const string LangDe    = "deu";
    public const string LangDeAd  = "deu-ad";
    public const string LangDeDgs = "deu-dgs";
    public const string LangEn    = "eng";
    public const string LangFr    = "fra";
}
