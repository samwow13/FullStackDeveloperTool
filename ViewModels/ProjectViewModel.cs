using FullStackLauncher.Models;

namespace FullStackLauncher.ViewModels;

// Sidebar state shares the service cards' live runners and is never saved to settings.
public sealed class ProjectViewModel(ProjectProfile profile, IReadOnlyList<ServiceViewModel> services) : ObservableObject
{
    private IReadOnlyList<ProjectBranchViewModel> _branches =
        [new("Checking branch…", "Reading Git information from the project and service folders.")];

    public ProjectProfile Profile { get; } = profile;
    public string Name => Profile.Name;
    public bool IsArchived => Profile.IsArchived;
    public IReadOnlyList<ServiceViewModel> Services { get; } = services;
    public IReadOnlyList<ProjectBranchViewModel> Branches => _branches;

    public void UpdateBranches(IReadOnlyList<ProjectBranchViewModel> branches)
    {
        if (_branches.SequenceEqual(branches)) return;
        _branches = branches;
        Changed(nameof(Branches));
    }

    public void RefreshName() => Changed(nameof(Name));
    public void RefreshArchiveState() => Changed(nameof(IsArchived));
}

public sealed record ProjectBranchViewModel(string DisplayText, string Detail);
