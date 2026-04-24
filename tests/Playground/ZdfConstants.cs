namespace Mediathek.Crawlers.Zdf;

public static class ZdfConstants
{
    // ── Auth ──────────────────────────────────────────────────────────────────
    // Hardcoded exactly as in ZdfCrawler.java — update when ZDF rotates it.
    public const string AuthKey    = "aa3noh4ohz9eeboo8shiesheec9ciequ9Quah7el";
    public const string AuthHeader = "Api-Auth";

    // appId embedded in GraphQL variables (from ZdfConstants.java URL_TOPIC_PAGE_NO_SEASON_VARIABLES)
    public const string AppId = "ffw-mt-web-879d5c17";

    // ── Base URLs ─────────────────────────────────────────────────────────────
    public const string ApiBase = "https://api.zdf.de";
    public const string WebBase = "https://www.zdf.de";

    // ── Pagination ────────────────────────────────────────────────────────────
    public const int LetterPageCount  = 27;   // tabs 0-26 (A-Z + special)
    public const int EpisodesPageSize = 24;
    public const string NoCursor      = "null";

    // ── Persisted query hashes ────────────────────────────────────────────────
    // These are stable query fingerprints baked into ZDF's GraphQL API.
    // Update if ZDF deploys a new API version and requests start failing.
    public const string HashLetterPage        = "63848395d2f977dbf99ce30172c8d80038a54615574295eee6f8704c5e6fcbee";
    public const string HashTopicSeason       = "9412a0f4ac55dc37d46975d461ec64bfd14380d815df843a1492348f77b5c99a";
    public const string HashSpecialCollection = "c85ca9c636258a65961a81124abd0dbef06ab97eaca9345cbdfde23b54117242";

    // ── Special collection IDs -> topic name ─────────────────────────────────
    // From ZdfConstants.java SPECIAL_COLLECTION_IDS
    public static readonly Dictionary<string, string> SpecialCollections = new()
    {
        ["pub-form-10004"] = "Filme",
        ["pub-form-10003"] = "Dokus",
        ["pub-form-10010"] = "Serien",
        ["genre-10290"]    = "Sport",
    };

    // ── Partner -> broadcaster key mapping ───────────────────────────────────
    // From ZdfConstants.java PARTNER_TO_SENDER
    public static readonly Dictionary<string, string> PartnerToKey = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ZDFinfo"] = "ZDFinfo",
        ["ZDFneo"]  = "ZDFneo",
        ["ZDF"]     = "ZDF",
        ["EMPTY"]   = "ZDF",
        ["ZDFtivi"] = "ZDFtivi",
    };

    // ── Language keys (from ZdfDownloadDtoDeserializer.java) ─────────────────
    public const string LangDe    = "deu";
    public const string LangDeAd  = "deu-ad";
    public const string LangDeDgs = "deu-dgs";
    public const string LangEn    = "eng";
    public const string LangFr    = "fra";

    // ── Quality string -> enum (from ZdfDownloadDtoDeserializer.java) ─────────
    // low/med/medium/high -> Sd
    // veryhigh/hd         -> Normal
    // fhd                 -> Hd
    // uhd                 -> Uhd
}

public static class ZdfUrlBuilder
{
    // Letter page: A-Z browse, tabIndex 0-26
    // From ZdfConstants.URL_LETTER_PAGE + URL_LETTER_PAGE_VARIABLES
    public static string LetterPage(int tabIndex, string cursor = "null")
    {
        var c    = cursor == "null" ? "null" : $"\"{cursor}\"";
        var vars = Uri.EscapeDataString(
            $"{{\"staticGridClusterPageSize\":6,\"staticGridClusterOffset\":0," +
            $"\"canonical\":\"sendungen-100\",\"endCursor\":{c}," +
            $"\"tabIndex\":{tabIndex}," +
            $"\"itemsFilter\":{{\"teaserUsageNotIn\":[\"TIVI_HBBTV_ONLY\"]}}}}");
        var ext  = Uri.EscapeDataString(
            $"{{\"persistedQuery\":{{\"version\":1,\"sha256Hash\":\"{ZdfConstants.HashLetterPage}\"}}}}");
        return $"{ZdfConstants.ApiBase}/graphql?operationName=specialPageByCanonical&variables={vars}&extensions={ext}";
    }

    // Topic/season: episodes for a show, with optional cursor
    // From ZdfConstants.URL_TOPIC_PAGE + URL_TOPIC_PAGE_VARIABLES / URL_TOPIC_PAGE_VARIABLES_WITH_CURSOR
    public static string TopicSeason(string canonical, int seasonIndex, int pageSize, string? cursor = null)
    {
        string vars = cursor is null
            ? $"{{\"seasonIndex\":{seasonIndex},\"episodesPageSize\":{pageSize}," +
              $"\"canonical\":\"{canonical}\"," +
              $"\"sortBy\":[{{\"field\":\"EDITORIAL_DATE\",\"direction\":\"DESC\"}}]}}"
            : $"{{\"seasonIndex\":{seasonIndex},\"episodesPageSize\":{pageSize}," +
              $"\"canonical\":\"{canonical}\"," +
              $"\"sortBy\":[{{\"field\":\"EDITORIAL_DATE\",\"direction\":\"DESC\"}}]," +
              $"\"episodesAfter\":\"{cursor}\"}}";

        var v = Uri.EscapeDataString(vars);
        var e = Uri.EscapeDataString(
            $"{{\"persistedQuery\":{{\"version\":1,\"sha256Hash\":\"{ZdfConstants.HashTopicSeason}\"}}}}");
        return $"{ZdfConstants.ApiBase}/graphql?operationName=seasonByCanonical&variables={v}&extensions={e}";
    }

    // Special collection (no season structure), with optional cursor
    // From ZdfConstants.URL_TOPIC_PAGE_NO_SEASON + URL_TOPIC_PAGE_NO_SEASON_VARIABLES
    public static string SpecialCollection(string collectionId, int pageSize, string cursor = "null")
    {
        var after = cursor == "null" ? "null" : $"\"{cursor}\"";
        var vars  = Uri.EscapeDataString(
            $"{{\"collectionId\":\"{collectionId}\"," +
            $"\"input\":{{\"appId\":\"{ZdfConstants.AppId}\"," +
            $"\"filters\":{{}}," +
            $"\"pagination\":{{\"first\":{pageSize},\"after\":{after}}}," +
            $"\"user\":{{\"abGroup\":\"gruppe-d\",\"userSegment\":\"segment_0\"}}," +
            $"\"tabId\":null}}}}");
        var ext = Uri.EscapeDataString(
            $"{{\"persistedQuery\":{{\"version\":1,\"sha256Hash\":\"{ZdfConstants.HashSpecialCollection}\"}}}}");
        return $"{ZdfConstants.ApiBase}/graphql?operationName=getMetaCollectionContent&variables={vars}&extensions={ext}";
    }

    // Day search: URL_DAY from ZdfConstants.java
    public static string DaySearch(DateOnly date)
    {
        var d = date.ToString("yyyy-MM-dd");
        return $"{ZdfConstants.ApiBase}/search/documents?hasVideo=true&q=*&types=page-video" +
               $"&sortOrder=desc&from={d}T00:00:00.000%2B01:00" +
               $"&to={d}T23:59:59.999%2B01:00&sortBy=date&page=1";
    }
}
