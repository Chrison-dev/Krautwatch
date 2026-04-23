namespace MediathekNext.Infrastructure.System;

// ──────────────────────────────────────────────────────────────
// Enums
// ──────────────────────────────────────────────────────────────

public enum AppState
{
    Initialising,
    Ready,
    Error
}

public enum StepStatus
{
    Pending,
    InProgress,
    Complete,
    Failed
}

// ──────────────────────────────────────────────────────────────
// Step snapshot — immutable, safe to serialise
// ──────────────────────────────────────────────────────────────

public record InitStep(
    string Name,
    StepStatus Status,
    string? Detail = null);

// ──────────────────────────────────────────────────────────────
// Status snapshot returned by the API
// ──────────────────────────────────────────────────────────────

public record SystemStatus(
    AppState State,
    long CatalogEntryCount,
    DateTimeOffset? LastRefreshedAt,
    string? CurrentTask,
    IReadOnlyList<InitStep> Steps,
    string? ErrorMessage = null);

// ──────────────────────────────────────────────────────────────
// Service — singleton, written by background services,
//           read by the API endpoint
// ──────────────────────────────────────────────────────────────

public class SystemStatusService
{
    private readonly object _lock = new();

    // Mutable state — always mutated under _lock, snapshots are immutable
    private AppState _state = AppState.Initialising;
    private string? _currentTask;
    private string? _errorMessage;
    private DateTimeOffset? _lastRefreshedAt;
    private long _catalogEntryCount;

    private readonly List<StepInfo> _steps =
    [
        new("Database",        StepStatus.Pending),
        new("Catalog refresh", StepStatus.Pending),
    ];

    // ── Public read ──────────────────────────────────────────

    public SystemStatus GetSnapshot()
    {
        lock (_lock)
        {
            return new SystemStatus(
                State:             _state,
                CatalogEntryCount: _catalogEntryCount,
                LastRefreshedAt:   _lastRefreshedAt,
                CurrentTask:       _currentTask,
                Steps:             _steps.Select(s => new InitStep(s.Name, s.Status, s.Detail)).ToList(),
                ErrorMessage:      _errorMessage);
        }
    }

    // ── Mutations called by background services ───────────────

    public void MarkDatabaseReady()
    {
        lock (_lock)
        {
            SetStep("Database", StepStatus.Complete);
            _currentTask = "Waiting for catalog refresh…";
        }
    }

    public void MarkCatalogStarting()
    {
        lock (_lock)
        {
            SetStep("Catalog refresh", StepStatus.InProgress, "Connecting to MediathekView…");
            _currentTask = "Starting catalog download…";
        }
    }

    public void MarkCatalogDownloading(int percent)
    {
        lock (_lock)
        {
            var detail = percent > 0
                ? $"Downloading filmliste… {percent}%"
                : "Downloading filmliste…";
            SetStep("Catalog refresh", StepStatus.InProgress, detail);
            _currentTask = detail;
        }
    }

    public void MarkCatalogParsing(long parsed, long? total)
    {
        lock (_lock)
        {
            var detail = total.HasValue
                ? $"Parsing entries: {parsed:N0} / ~{total.Value:N0}"
                : $"Parsing entries: {parsed:N0}…";
            SetStep("Catalog refresh", StepStatus.InProgress, detail);
            _currentTask = detail;
            _catalogEntryCount = parsed;
        }
    }

    public void MarkCatalogReady(long entryCount, DateTimeOffset refreshedAt)
    {
        lock (_lock)
        {
            SetStep("Catalog refresh", StepStatus.Complete, $"{entryCount:N0} entries loaded");
            _catalogEntryCount = entryCount;
            _lastRefreshedAt   = refreshedAt;
            _currentTask       = null;
            _state             = AppState.Ready;
        }
    }

    public void MarkCatalogRefreshing()
    {
        // Called on subsequent refreshes — app stays Ready, just updates the step detail
        lock (_lock)
        {
            SetStep("Catalog refresh", StepStatus.InProgress, "Refreshing catalog…");
            _currentTask = "Refreshing catalog in background…";
        }
    }

    public void MarkError(string step, string message)
    {
        lock (_lock)
        {
            SetStep(step, StepStatus.Failed, message);
            _errorMessage = message;
            // Don't flip to Error state if catalog is already ready — a refresh failure
            // is non-fatal; the app stays usable with stale data
            if (_state != AppState.Ready)
                _state = AppState.Error;
        }
    }

    // Allow recovering from a transient error on retry
    public void ClearError()
    {
        lock (_lock)
        {
            _errorMessage = null;
            if (_state == AppState.Error)
                _state = AppState.Initialising;
        }
    }

    // ── Private helpers ───────────────────────────────────────

    private void SetStep(string name, StepStatus status, string? detail = null)
    {
        var step = _steps.FirstOrDefault(s => s.Name == name);
        if (step is null) return;
        step.Status = status;
        step.Detail = detail;
    }

    private class StepInfo(string name, StepStatus status)
    {
        public string Name     { get; } = name;
        public StepStatus Status { get; set; } = status;
        public string? Detail  { get; set; }
    }
}
