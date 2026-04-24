namespace Mediathek.Crawlers.Ard;

public static class ArdConstants
{
    public const string ApiBase    = "https://api.ardmediathek.de";
    public const string ProgramApi = "https://programm-api.ard.de";

    // ── URL templates (from ArdConstants.java) ────────────────────────────────

    // Single item detail page (used by ArdFilmDetailTask via ITEM_URL)
    public const string ItemUrl = ApiBase + "/page-gateway/pages/ard/item/{0}?embedded=true&mcV6=true";

    // A-Z topics overview per client
    public const string TopicsUrl = ApiBase + "/page-gateway/pages/{0}/editorial/experiment-a-z?embedded=false";

    // Topic group compilations (widgets -> editorials)
    public const string TopicsCompilationUrl = ApiBase + "/page-gateway/widgets/{0}/editorials/{1}?pageNumber=0&pageSize={2}";
    public const int    TopicsCompilationPageSize = 100;

    // Individual topic episodes
    public const string TopicUrl      = ApiBase + "/page-gateway/widgets/ard/asset/{0}?pageSize={1}";
    public const int    TopicPageSize = 50;

    // Day program EPG
    public const string DayPageUrl = ProgramApi + "/program/api/program?day={0}&channelIds={1}&mode=channel";

    // Website URL for a video ID
    public const string WebsiteUrl = "https://www.ardmediathek.de/video/{0}";

    // ── Clients ───────────────────────────────────────────────────────────────

    // Day-page clients (from ArdConstants.CLIENTS_DAY)
    public static readonly string[] DayClients =
    [
        "daserste", "br", "hr", "mdr", "ndr", "radiobremen",
        "rbb", "sr", "swr", "wdr", "one", "alpha", "tagesschau24", "phoenix"
    ];

    // Topic-crawl clients (DayClients + funk; from ArdConstants.CLIENTS)
    public static readonly string[] TopicClients =
    [
        "ard", "daserste", "br", "hr", "mdr", "ndr", "radiobremen",
        "rbb", "sr", "swr", "wdr", "one", "alpha", "tagesschau24", "phoenix", "funk"
    ];

    // ── Client -> broadcaster key ─────────────────────────────────────────────
    // From ArdFilmDeserializer.java ADDITIONAL_SENDER (publicationService.partner -> Const.XXX)
    public static readonly Dictionary<string, string> PartnerToKey =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["rbb"]          = "RBB",
            ["swr"]          = "SWR",
            ["mdr"]          = "MDR",
            ["ndr"]          = "NDR",
            ["wdr"]          = "WDR",
            ["hr"]           = "HR",
            ["br"]           = "BR",
            ["radio_bremen"] = "RB",
            ["tagesschau24"] = "tagesschau24",
            ["das_erste"]    = "ARD",
            ["one"]          = "ONE",
            ["ard-alpha"]    = "ARDalpha",
            ["funk"]         = "funk",
            ["sr"]           = "SR",
            ["phoenix"]      = "phoenix",
            ["ard"]          = "ARD",
        };

    // Senders from the ARD API that belong to other broadcasters — skip them
    public static readonly HashSet<string> IgnoredSenders =
        new(StringComparer.OrdinalIgnoreCase) { "zdf", "kika", "3sat", "arte" };
}
