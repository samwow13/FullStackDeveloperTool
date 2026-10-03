using FullStackLauncher.Models;

namespace FullStackLauncher.ViewModels;

/// <summary>Aggregate metadata from the latest completed inventory, retained for this service session.</summary>
public sealed class ApiEndpointCountViewModel : ObservableObject
{
    private int? _total;
    private bool _partial;
    private string _details = "";

    public bool IsVisible => _total.HasValue;
    public string Summary => _total is { } total
        ? $"{total:N0} API endpoints" + (_partial ? " · partial" : "") : "";
    public string Foreground => _partial ? "#FFD27A" : "#CCD7E7";
    public string Details => _details;

    public void SetInventory(ApiEndpointInventory? inventory)
    {
        _total = inventory?.TotalOperationCount;
        _partial = inventory?.IsPartial == true;
        _details = inventory is null ? "" :
            $"{inventory.TotalOperationCount:N0} discovered endpoint operations across all HTTP methods.\n" +
            $"{inventory.SourceLabel}\nCounted {DateTimeOffset.Now:g}.\n\n" +
            ApiEndpointInventory.CountingSemantics +
            (inventory.IsPartial ? "\n\nPartial inventory. Review limitations in API endpoints…" : "");
        Changed(nameof(IsVisible));
        Changed(nameof(Summary));
        Changed(nameof(Foreground));
        Changed(nameof(Details));
    }
}
