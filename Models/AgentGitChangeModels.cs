namespace FullStackLauncher.Models;

public sealed record AgentGitConnectionSelection(string RemoteName, string ConnectionId);

public sealed record AgentGitSummaryEntry
{
    public string Id { get; init; } = "";
    public string UpdateId { get; init; } = "";
    public IReadOnlyList<string> Bullets { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ViewedAt { get; init; }
    public string? ConsumedCommitId { get; init; }
}

public sealed record AgentGitSummaryBatch
{
    public string RepositoryRoot { get; init; } = "";
    public string Branch { get; init; } = "";
    public string RemoteName { get; init; } = "";
    public string ConnectionId { get; init; } = "";
    public IReadOnlyList<AgentGitSummaryEntry> Entries { get; init; } = [];

    public string ComposeMessage(string title) => GitCommitMessage.Compose(title, this);
}

/// <summary>Composes the complete message shared by commit review and the dashboard preview.</summary>
public static class GitCommitMessage
{
    public const int MaximumLength = 4000;

    public static string ComposeUpdate(string branch, AgentGitSummaryBatch? summaries = null) =>
        Compose($"Update {branch}", summaries);

    public static string Compose(string title, AgentGitSummaryBatch? summaries = null)
    {
        var bullets = summaries is null ? Array.Empty<string>() : NewestFirst(summaries).SelectMany(entry => entry.Bullets)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return bullets.Length == 0 ? title : title + "\n\n" + string.Join("\n", bullets.Select(bullet => "- " + bullet));
    }

    public static AgentGitSummaryEntry[] NewestFirst(AgentGitSummaryBatch summaries) =>
        // Ledger insertion order breaks ties between reports saved at the same timestamp.
        summaries.Entries.Reverse().OrderByDescending(entry => entry.CreatedAt).ToArray();
}
