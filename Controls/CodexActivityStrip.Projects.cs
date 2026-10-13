using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher.Controls;

public partial class CodexActivityStrip
{
    private IReadOnlyList<CodexActivityProjectScope> _projects = [];
    private string? _selectedProjectId;
    private string? _selectedProjectName;
    private CodexActivityFeedSnapshot? _allProjectsSnapshot;
    private int _projectScopeVersion;
    private bool _projectRefreshPending;

    internal string? FindAgentWorkTitle(string projectId, string? chatId)
    {
        if (chatId is null || _allProjectsSnapshot is not { IsAvailable: true } snapshot) return null;
        var agent = snapshot.Agents.Where(root => root.ProjectId == projectId)
            .SelectMany(root => root.Subagents.Prepend(root))
            .FirstOrDefault(candidate => string.Equals(candidate.Id, chatId, StringComparison.Ordinal));
        return agent is null || string.IsNullOrWhiteSpace(agent.Title)
            || agent.Title is "Codex agent" or "Codex chat" or "Codex task" or "Chat unavailable"
            ? null : agent.Title;
    }

    public void SetProjectScope(string? projectId, string? projectName,
        IReadOnlyList<CodexActivityProjectScope> projects)
    {
        _projects = projects;
        _selectedProjectId = projectId;
        _selectedProjectName = projectName;
        _projectScopeVersion++;
        CloseMenus();
        HideThoughtPopups();
        // Clear first even after a failed read. Old project cards must never
        // remain visible while the newly selected project's status is loading.
        Agents.Clear();
        _hasAppliedSnapshot = false;
        _firstVisibleCard = 0;
        AgentScroller.ScrollToHorizontalOffset(0);
        if (_allProjectsSnapshot is { } snapshot)
            ApplyProject(snapshot.ForProject(projectId, projectName));
        else NotifyView();
        if (_refreshing) _projectRefreshPending = true;
        else _ = RefreshAsync();
    }
}
