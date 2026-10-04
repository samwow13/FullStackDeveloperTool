using FullStackLauncher.Models;

namespace FullStackLauncher.ViewModels;

/// <summary>Aggregate metadata from the latest completed inventory, retained for this service session.</summary>
public sealed class ApiEndpointCountViewModel : ObservableObject
{
    private int? _total;
    private bool _partial;
    private string _details = "";
    private bool _busy;
    private bool _needsBranch;
    private string? _error;

    public bool HasLoaded { get; private set; }
    public bool IsBusy => _busy;
    public bool NeedsBranch => _needsBranch;
    public bool CanCount => !_busy && !_needsBranch;
    public bool HasError => _error is not null && !_needsBranch;
    public bool HasResult => !_busy && !_needsBranch && _error is null && _total.HasValue;
    public bool IsVisible => HasResult || _busy || _needsBranch || HasError;
    public string Value => HasResult ? $"{_total!.Value:N0}" + (_partial ? " · partial" : "") : "—";
    public string ButtonLabel => _busy ? "Counting…" : _total.HasValue ? "Recount" : "Count";
    public string Status => _busy ? "Counting…" : _needsBranch ? "Select a branch." : _error ?? "";
    public string Summary => HasResult && _total is { } total
        ? $"{total:N0} API endpoints" + (_partial ? " · partial" : "") : "";
    public string Foreground => _partial || HasError || _needsBranch ? "#FFD27A" : "#CCD7E7";
    public string Details => string.Join("\n\n", new[] { _details, _error }.Where(text => !string.IsNullOrEmpty(text)));

    public bool BeginCount()
    {
        if (_busy) return false;
        _busy = true;
        _needsBranch = false;
        _error = null;
        Notify();
        return true;
    }

    public void Complete(ApiEndpointInventory inventory, string branch)
    {
        _busy = false;
        SetInventoryCore(inventory, branch);
    }

    public void Fail(string message, bool needsBranch = false)
    {
        _busy = false;
        _needsBranch = needsBranch;
        _error = message;
        HasLoaded = true;
        Notify();
    }

    public void Cancel()
    {
        _busy = false;
        _needsBranch = false;
        _error = null;
        HasLoaded = false;
        Notify();
    }

    public void Reset()
    {
        _busy = false;
        SetInventoryCore(null);
    }

    public void SetInventory(ApiEndpointInventory? inventory)
    {
        // A details-window refresh must not replace a count still in progress.
        if (_busy) return;
        SetInventoryCore(inventory);
    }

    private void SetInventoryCore(ApiEndpointInventory? inventory, string? branch = null)
    {
        _total = inventory?.TotalOperationCount;
        _partial = inventory?.IsPartial == true;
        _needsBranch = false;
        _error = null;
        HasLoaded = inventory is not null;
        _details = inventory is null ? "" :
            $"{inventory.TotalOperationCount:N0} discovered endpoint operations across all HTTP methods.\n" +
            (branch is null ? "" : $"Branch: {branch}\n") +
            $"{inventory.SourceLabel}\nCounted {DateTimeOffset.Now:g}.\n\n" +
            ApiEndpointInventory.CountingSemantics +
            (inventory.IsPartial ? "\n\nPartial inventory. Review limitations in API endpoints…" : "");
        Notify();
    }

    private void Notify()
    {
        Changed(nameof(HasLoaded));
        Changed(nameof(IsBusy));
        Changed(nameof(NeedsBranch));
        Changed(nameof(CanCount));
        Changed(nameof(HasError));
        Changed(nameof(HasResult));
        Changed(nameof(Value));
        Changed(nameof(ButtonLabel));
        Changed(nameof(Status));
        Changed(nameof(IsVisible));
        Changed(nameof(Summary));
        Changed(nameof(Foreground));
        Changed(nameof(Details));
    }
}
