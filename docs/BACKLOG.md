# Backlog

Unplanned items that aren't part of an active epic yet.

---

## Crawlers

### ZDF `CrawlRecentAsync` fetches all episodes instead of just recent ones

**Context:** `ZdfCrawler.CrawlRecentAsync` uses the day-search endpoint to find shows that aired on a given day, then calls `CrawlTopicRefAsync` for each result — which fetches the **entire season** of every show, not just the episode(s) that aired. For each of those episodes it then makes an additional HTTP call to resolve download URLs. Even with `daysPast: 1` this can result in thousands of sequential HTTP requests, making a "recent" crawl as slow as a full one.

**Fix:** Parse the episode directly from the day-search result (or restrict `CrawlTopicRefAsync` to the specific episode canonical) rather than re-crawling the whole show's season.
