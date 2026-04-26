namespace MediathekNext.Crawlers.Zdf;

internal static class ZdfUrlBuilder
{
    public static string LetterPage(int tabIndex, string? cursor)
    {
        var c    = cursor is null ? "null" : $"\"{cursor}\"";
        var vars = Uri.EscapeDataString(
            $"{{\"staticGridClusterPageSize\":6,\"staticGridClusterOffset\":0," +
            $"\"canonical\":\"sendungen-100\",\"endCursor\":{c}," +
            $"\"tabIndex\":{tabIndex}," +
            $"\"itemsFilter\":{{\"teaserUsageNotIn\":[\"TIVI_HBBTV_ONLY\"]}}}}");
        var ext = Uri.EscapeDataString(
            $"{{\"persistedQuery\":{{\"version\":1,\"sha256Hash\":\"{ZdfConstants.HashLetterPage}\"}}}}");
        return $"{ZdfConstants.ApiBase}/graphql?operationName=specialPageByCanonical&variables={vars}&extensions={ext}";
    }

    public static string TopicSeason(string canonical, int seasonIndex, int pageSize, string? cursor)
    {
        var vars = cursor is null
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

    public static string SpecialCollection(string collectionId, int pageSize, string? cursor)
    {
        var after = cursor is null ? "null" : $"\"{cursor}\"";
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

    public static string DaySearch(DateOnly date)
    {
        var d = date.ToString("yyyy-MM-dd");
        return $"{ZdfConstants.ApiBase}/search/documents?hasVideo=true&q=*&types=page-video" +
               $"&sortOrder=desc&from={d}T00:00:00.000%2B01:00" +
               $"&to={d}T23:59:59.999%2B01:00&sortBy=date&page=1";
    }
}
