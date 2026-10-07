using FullStackLauncher.CodexMonitor;

namespace FullStackLauncher.Services;

public sealed partial class CodexActivityFeedService
{
    private void ReadProjectEvidence(IReadOnlyList<CodexActivityProjectScope> projects,
        IReadOnlyList<AgentSnapshot> agents, CancellationToken token)
    {
        // Refresh passive repository metadata off the dispatcher; selection never
        // changes the global reader or the observed completion state.
        _projectResolver = new(projects);
        var mappings = _projectReceiptReader.Read(token)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var pair in _projectStore.Read(token)) mappings[pair.Key] = pair.Value;
        _explicitProjectIds = mappings;
        var requested = agents.Where(agent =>
                _projectResolver.Resolve(new(agent.Id, agent.ProjectPath), [], mappings) is null)
            .OrderBy(agent => agent.State is AgentRunState.Running or AgentRunState.NeedsInput ? 0 : 1)
            .Select(agent => agent.Id).ToArray();
        _workingFolders = _workingFolderReader.Read(agents, requested, token);
    }

    private void UpdateProjectOwnership()
    {
        var all = _familyAnchors.Values.Concat(_tracked.Values.Select(item => item.Agent))
            .GroupBy(agent => agent.Id, StringComparer.Ordinal).Select(group => group.Last()).ToArray();
        var owners = new Dictionary<string, CodexActivityProjectScope>(StringComparer.Ordinal);
        foreach (var family in all.GroupBy(agent => ResolveFamilyRoot(agent).Id, StringComparer.Ordinal))
        {
            CodexActivityProjectMember Member(AgentSnapshot agent) => new(agent.Id, agent.ProjectPath)
            {
                WorkingFolders = _workingFolders.GetValueOrDefault(agent.Id) ?? []
            };
            var rootMember = TryGetFamilyRoot(family.Key, out var root) ? Member(root) : new(family.Key, "");
            var owner = _projectResolver.Resolve(rootMember, family.Select(Member).ToArray(), _explicitProjectIds);
            if (owner is not null) owners[family.Key] = owner;
        }
        _projectOwners = owners;
    }
}
