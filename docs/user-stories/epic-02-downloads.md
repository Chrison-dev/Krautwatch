# Epic 02 — Downloads

## US-004 · Manual Download

> As a user, I want to trigger a download for a specific episode so that I can watch it offline.

**Acceptance Criteria:**
- User can select quality (HD, SD) before downloading
- Download is added to the queue immediately with status "Queued"
- Progress is visible (%, speed, ETA)
- Completed download shows file size and save path
- Failed download shows a clear error reason — never silent

---

## US-005 · View Download Queue

> As a user, I want to see all active, queued, and recently completed downloads so that I can track progress.

**Acceptance Criteria:**
- List shows status, progress, channel, and title for each item
- Completed and failed items are retained for at least 24 hours
- User can cancel a queued or in-progress download
- User can retry a failed download
