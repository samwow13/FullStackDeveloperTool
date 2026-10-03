using FullStackLauncher.Services;

namespace FullStackLauncher.ViewModels;

public sealed class SourceLineCountViewModel : ObservableObject
{
    private SourceLineCountSnapshot? _snapshot;
    private bool _loading;
    private bool _counting;
    private string? _error;

    public bool HasLoaded { get; private set; }
    public bool CanCount => !_loading && !_counting;
    public bool IsVisible => _snapshot is not null || _loading || _counting || _error is not null;
    public bool HasAverage => _snapshot?.HasCurrentScope == true;
    public string AverageSummary => !HasAverage ? "" : _snapshot!.Current.Files == 0
        ? "Avg n/a" : $"Avg {(decimal)_snapshot.Current.Lines / _snapshot.Current.Files:N1} lines/file";
    public string ButtonLabel => _loading ? "Loading count…" : _counting ? "Counting…"
        : _snapshot is null ? "Count code lines" : "Recount code lines";
    public string Summary
    {
        get
        {
            var count = _snapshot is null ? "" : $"{_snapshot.Current.Lines:N0} lines";
            if (_snapshot?.HasCurrentScope == true && _snapshot.PreviousLines is { } previous)
            {
                var delta = _snapshot.Current.Lines - previous;
                count += delta == 0 ? " · 0 change" : $" · {delta:+#,0;-#,0;0}";
            }
            if (_snapshot?.HasCurrentScope == false) count += " · recount needed";
            var state = _loading ? "Loading count…" : _counting ? "Counting…"
                : _error is not null ? (_snapshot is null ? "Count unavailable" : "Recount failed") : "";
            return count.Length == 0 ? state : state.Length == 0 ? count : $"{count} · {state}";
        }
    }
    public string Foreground => _error is null ? "#CCD7E7" : "#FFD27A";
    public string Details
    {
        get
        {
            var detail = SourceLineCounter.ScopeDescription;
            if (_snapshot is { } snapshot)
            {
                detail += $"\n\nLast successful count: {snapshot.Current.Lines:N0} lines in {snapshot.Current.Files:N0} files, "
                    + $"{snapshot.Current.CountedAtUtc.ToLocalTime():g}.";
                if (!snapshot.HasCurrentScope)
                {
                    detail += "\nThis saved count uses earlier exclusions and may include package.json. Recount to apply current exclusions, show the average, and establish a new baseline.";
                }
                else
                {
                    detail += snapshot.Current.Files == 0
                        ? "\nAverage file size is unavailable because no source files were counted."
                        : $"\nAverage file size: {(decimal)snapshot.Current.Lines / snapshot.Current.Files:N1} physical lines per counted file. Empty files are included.";
                    detail += snapshot.PreviousLines is { } previous
                        ? $"\nPrevious count: {previous:N0} lines. Change: {snapshot.Current.Lines - previous:+#,0;-#,0;0} lines."
                        : "\nBaseline for current exclusions; no previous value to compare.";
                }
            }
            if (_counting) detail += "\n\nCounting this service's configured folder. The previous count stays visible until the new count is saved.";
            if (_error is not null) detail += "\n\n" + _error;
            return detail;
        }
    }

    public bool BeginLoad()
    {
        if (HasLoaded || _loading || _counting) return false;
        _loading = true;
        Notify();
        return true;
    }

    public void BeginCount()
    {
        _counting = true;
        _error = null;
        Notify();
    }

    public void Complete(SourceLineCountSnapshot? snapshot)
    {
        _snapshot = snapshot;
        _error = null;
        Finish();
    }

    public void Fail(string message)
    {
        _error = message;
        Finish();
    }

    public void Cancel()
    {
        _error = null;
        Finish();
    }

    private void Finish()
    {
        _loading = false;
        _counting = false;
        HasLoaded = true;
        Notify();
    }

    private void Notify()
    {
        Changed(nameof(CanCount));
        Changed(nameof(IsVisible));
        Changed(nameof(HasAverage));
        Changed(nameof(AverageSummary));
        Changed(nameof(ButtonLabel));
        Changed(nameof(Summary));
        Changed(nameof(Foreground));
        Changed(nameof(Details));
    }
}
