using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher.ViewModels;

// Sidebar state shares the service cards' live runners and is never saved to settings.
public sealed class ProjectViewModel(ProjectProfile profile, IReadOnlyList<ServiceViewModel> services) : ObservableObject
{
    private IReadOnlyList<ServiceViewModel> _services = services.ToArray();
    private IReadOnlyList<ProjectBranchViewModel> _branches =
        [new("Checking branch…", "Reading Git information from the project and service folders.")];
    private bool _isDetailsExpanded;
    private ProjectAgentActivity _agentActivity;

    public ProjectProfile Profile { get; } = profile;
    public string Name => Profile.Name;
    public string CompactName
    {
        get
        {
            var words = Name.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
            var initials = words.Length > 1 ? string.Concat(words.Take(3).Select(word => word[0]))
                : string.Concat(Name.Where(char.IsUpper).Take(3));
            return (initials.Length > 1 ? initials : string.Concat(Name.Trim().Take(2))).ToUpperInvariant();
        }
    }
    public bool IsArchived => Profile.IsArchived;
    public IReadOnlyList<ServiceViewModel> Services => _services;
    public IReadOnlyList<ProjectBranchViewModel> Branches => _branches;
    public int RunningAgentCount => _agentActivity.RunningCount;
    public bool HasUnreadAgentCompletion => _agentActivity.HasUnreadCompletion;
    public string AgentActivityDescription
    {
        get
        {
            if (!_agentActivity.HasSnapshot) return "Checking Codex agent activity.";
            var count = $"{RunningAgentCount} {(RunningAgentCount == 1 ? "agent" : "agents")} running.";
            return (_agentActivity.IsAvailable ? count : "Last observed: " + count) +
                (HasUnreadAgentCompletion ? " Completed agent work; click this project to review." : "");
        }
    }
    public bool IsDetailsExpanded
    {
        get => _isDetailsExpanded;
        set { if (_isDetailsExpanded == value) return; _isDetailsExpanded = value; Changed(); }
    }

    public void UpdateBranches(IReadOnlyList<ProjectBranchViewModel> branches)
    {
        if (_branches.SequenceEqual(branches)) return;
        _branches = branches;
        Changed(nameof(Branches));
    }

    public void UpdateAgentActivity(ProjectAgentActivity activity)
    {
        if (_agentActivity == activity) return;
        var previous = _agentActivity;
        _agentActivity = activity;
        if (previous.RunningCount != activity.RunningCount) Changed(nameof(RunningAgentCount));
        if (previous.HasUnreadCompletion != activity.HasUnreadCompletion) Changed(nameof(HasUnreadAgentCompletion));
        Changed(nameof(AgentActivityDescription));
    }

    public void RefreshName()
    {
        Changed(nameof(Name));
        Changed(nameof(CompactName));
    }
    public void UpdateServices(IReadOnlyList<ServiceViewModel> services)
    {
        _services = services.ToArray();
        Changed(nameof(Services));
    }
    public void RefreshArchiveState() => Changed(nameof(IsArchived));
}

public sealed record ProjectBranchViewModel(string DisplayText, string Detail);
