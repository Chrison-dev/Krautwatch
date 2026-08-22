using System.Net;
using System.Text;
using Krautwatch.Infrastructure.Crawling.Ard;
using Shouldly;
using Xunit;

namespace Krautwatch.Infrastructure.Tests;

/// <summary>
/// Walking an ARD show's episode list past what the page embeds (#9). The page response carries only a
/// slice of a large widget — tagesschau's "Bundestag und Parlamente" reports 1588 items and embeds 35 —
/// so reading the embedded teasers alone quietly hides most of a show's history.
/// </summary>
public class ArdPaginationTests
{
    [Fact]
    public void Paging_rewrites_the_link_the_api_gave_us_rather_than_building_one()
    {
        // The issue proposed /widgets/{scope}/asset/{id}; that answers 400. The widget's own self link
        // is the route that works, so paging edits its query and leaves the rest alone.
        var self = "https://api.ardmediathek.de/page-gateway/widgets/ard/editorials/abc%3A123"
                 + "?pageNumber=0&pageSize=100&embedded=true";

        var paged = ArdCatalogClient.WithPaging(self, pageNumber: 3, pageSize: 50);

        paged.ShouldStartWith("https://api.ardmediathek.de/page-gateway/widgets/ard/editorials/abc%3A123?");
        paged.ShouldContain("embedded=true");
        paged.ShouldContain("pageNumber=3");
        paged.ShouldContain("pageSize=50");
        paged.ShouldNotContain("pageNumber=0");
    }

    [Fact]
    public async Task Episodes_beyond_the_embedded_slice_are_fetched()
    {
        // 5 items exist; the page embeds 2. Before #9 the other 3 were invisible.
        var handler = new ArdStubHandler(totalElements: 5, embedded: 2, pageSize: 2);
        var client = new ArdCatalogClient(new HttpClient(handler), new ArdOptions { PageSize = 2 });

        var episodes = await client.GetFullEpisodesAsync(Show(), TestContext.Current.CancellationToken);

        episodes.Count.ShouldBe(5);
        episodes.Select(e => e.Id).ShouldBeUnique();
    }

    [Fact]
    public async Task A_show_that_fits_in_the_embedded_slice_costs_no_extra_request()
    {
        var handler = new ArdStubHandler(totalElements: 2, embedded: 2, pageSize: 100);
        var client = new ArdCatalogClient(new HttpClient(handler), new ArdOptions { PageSize = 100 });

        var episodes = await client.GetFullEpisodesAsync(Show(), TestContext.Current.CancellationToken);

        episodes.Count.ShouldBe(2);
        handler.Requests.Count.ShouldBe(1);   // the show page, and nothing more
    }

    [Fact]
    public async Task The_cap_stops_a_four_figure_widget_from_becoming_a_four_figure_crawl()
    {
        var handler = new ArdStubHandler(totalElements: 1588, embedded: 35, pageSize: 100);
        var client = new ArdCatalogClient(new HttpClient(handler),
            new ArdOptions { PageSize = 100, MaxEpisodesPerShow = 200 });

        var episodes = await client.GetFullEpisodesAsync(Show(), TestContext.Current.CancellationToken);

        // Newest-first, so the cap keeps the part an indexer wants. Each of these costs an item-page
        // fetch downstream, which is why it is capped at all.
        episodes.Count.ShouldBe(200);
        handler.Requests.Count.ShouldBeLessThanOrEqualTo(4);
    }

    [Fact]
    public async Task A_repeating_page_ends_the_walk_instead_of_looping()
    {
        // A list that shifts under us can serve the same teasers twice; ids are the guard.
        var handler = new ArdStubHandler(totalElements: 500, embedded: 2, pageSize: 2, repeatSamePage: true);
        var client = new ArdCatalogClient(new HttpClient(handler), new ArdOptions { PageSize = 2 });

        var episodes = await client.GetFullEpisodesAsync(Show(), TestContext.Current.CancellationToken);

        episodes.Count.ShouldBe(2);
        handler.Requests.Count.ShouldBeLessThan(5);
    }

    private static ArdShow Show() => new(
        "Extra 3", "7tYDgyn04tGMb2oXIElK6L",
        "https://api.ardmediathek.de/page-gateway/pages/ard/editorial/extra-3?embedded=true", "NDR");

    /// <summary>
    /// Serves an ARD show page whose "Ganze Folgen" widget declares more items than it embeds, then
    /// serves the widget's own paging link.
    /// </summary>
    private sealed class ArdStubHandler(
        int totalElements, int embedded, int pageSize, bool repeatSamePage = false) : HttpMessageHandler
    {
        private const string WidgetSelf =
            "https://api.ardmediathek.de/page-gateway/widgets/ard/editorials/w%3A1?pageNumber=0&pageSize=100&embedded=true";

        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);

            var body = url.Contains("/widgets/")
                ? WidgetPage(PageNumberOf(url))
                : ShowPage();

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }

        private static int PageNumberOf(string url)
        {
            var marker = url.IndexOf("pageNumber=", StringComparison.Ordinal) + "pageNumber=".Length;
            var end = url.IndexOf('&', marker);
            return int.Parse(end < 0 ? url[marker..] : url[marker..end]);
        }

        private string ShowPage() =>
            $$"""
              {
                "widgets": [{
                  "title": "Ganze Folgen",
                  "type": "gridlist",
                  "pagination": { "pageNumber": 0, "pageSize": 100, "totalElements": {{totalElements}} },
                  "links": { "self": { "href": "{{WidgetSelf}}" } },
                  "teasers": [{{Teasers(0, embedded)}}]
                }]
              }
              """;

        private string WidgetPage(int pageNumber)
        {
            var start = repeatSamePage ? 0 : pageNumber * pageSize;
            var count = Math.Min(pageSize, Math.Max(0, totalElements - start));

            return $$"""
                     {
                       "pagination": { "pageNumber": {{pageNumber}}, "pageSize": {{pageSize}}, "totalElements": {{totalElements}} },
                       "teasers": [{{Teasers(start, count)}}]
                     }
                     """;
        }

        private static string Teasers(int start, int count) =>
            string.Join(",", Enumerable.Range(start, count).Select(i =>
                $$"""
                  {
                    "id": "ep-{{i}}",
                    "longTitle": "Episode {{i}}",
                    "duration": 1800,
                    "links": { "target": { "href": "https://api.ardmediathek.de/page-gateway/pages/ard/item/ep-{{i}}" } }
                  }
                  """));
    }
}
