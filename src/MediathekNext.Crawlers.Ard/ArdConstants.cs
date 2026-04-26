namespace MediathekNext.Crawlers.Ard;

internal static class ArdConstants
{
    public const string ApiBase    = "https://api.ardmediathek.de";
    public const string ProgramApi = "https://programm-api.ard.de";

    public const string ItemUrl               = ApiBase + "/page-gateway/pages/ard/item/{0}?embedded=true&mcV6=true";
    public const string TopicsUrl             = ApiBase + "/page-gateway/pages/{0}/editorial/experiment-a-z?embedded=false";
    public const string TopicsCompilationUrl  = ApiBase + "/page-gateway/widgets/{0}/editorials/{1}?pageNumber=0&pageSize={2}";
    public const int    TopicsCompilationPageSize = 100;
    public const string DayPageUrl            = ProgramApi + "/program/api/program?day={0}&channelIds={1}&mode=channel";
    public const string WebsiteUrl            = "https://www.ardmediathek.de/video/{0}";

    public static readonly string[] DayClients =
    [
        "daserste", "br", "hr", "mdr", "ndr", "radiobremen",
        "rbb", "sr", "swr", "wdr", "one", "alpha", "tagesschau24", "phoenix"
    ];

    public static readonly string[] TopicClients =
    [
        "ard", "daserste", "br", "hr", "mdr", "ndr", "radiobremen",
        "rbb", "sr", "swr", "wdr", "one", "alpha", "tagesschau24", "phoenix", "funk"
    ];

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
}
