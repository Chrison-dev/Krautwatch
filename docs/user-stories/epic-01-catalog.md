# Epic 01 — Catalog

## US-001 · Browse Catalog

> As a user, I want to browse available shows and episodes from ARD and ZDF so that I can discover content to download.

**Acceptance Criteria:**
- Catalog displays shows grouped by channel
- Each entry shows title, channel, broadcast date, duration
- Catalog refreshes on a configurable interval (default: every 6h)
- Last refresh time is always visible to the user

---

## US-002 · Search Catalog

> As a user, I want to search the catalog by title or keyword so that I can quickly find a specific show or episode.

**Acceptance Criteria:**
- Full-text search across title and description
- Results show channel, date, and duration
- Search returns results within 2 seconds for up to 50,000 entries
- Empty state shows a helpful message, not a blank page

---

## US-003 · View Episode Detail

> As a user, I want to see the details of an episode so that I can decide whether to download it.

**Acceptance Criteria:**
- Shows title, description, channel, broadcast date, duration, available quality levels
- Clear "Download" call-to-action per quality option
- Shows availability expiry if known

---

## US-007 · Browse by Show

> As a user, I want to see all episodes of a specific show so I can watch a series in order or find a missed episode.

**Acceptance Criteria:**
- Clicking a show title lists all available episodes for that show
- Episodes ordered by broadcast date descending
- Shows episode count and date range
- Works for series (Tatort), news (Tagesschau), and documentary series alike

---

## US-008 · Browse by Content Type

> As a user, I want to filter the catalog by content type (Movies, Series, Documentaries) so I can find the kind of content I'm in the mood for.

**Acceptance Criteria:**
- Four content type filters: Movies, Series/Episodes, Documentaries
- ContentType is visible on every catalog card
- Filters can be combined with channel filter and search
- "Movies" surfaces standalone films, not series episodes
