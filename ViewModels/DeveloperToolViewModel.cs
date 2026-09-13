using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher.ViewModels;

public sealed class DeveloperToolViewModel(DeveloperTool tool) : ObservableObject
{
    private DeveloperToolTarget? _target;
    private string _detail = "Finding shortcut…";
    private bool _opening;
    public DeveloperTool Profile { get; } = tool;
    public string Name => Profile.Name;
    public string Label => Profile.Kind == "Website" ? "Website" : Profile.Kind == "PgAdmin" ? "Database administration" : "Desktop app";
    public string Detail => _detail;
    public string Status => _opening ? "Opening…" : _target is not null ? "Ready" : "Set location";
    public bool CanOpen => _target is not null && !_opening;
    public DeveloperToolTarget? Target => _target;
    public bool IsOpening { get => _opening; set { _opening = value; Update(); } }
    public void SetTarget(DeveloperToolTarget? target, string? error = null)
    {
        _target = target;
        _detail = error ?? target?.Description ?? "Choose a location in Manage tools.";
        Update();
    }
    private void Update()
    {
        Changed(nameof(Detail)); Changed(nameof(Status)); Changed(nameof(CanOpen)); Changed(nameof(IsOpening));
    }
}
