using FullStackLauncher.Models;

namespace FullStackLauncher.Services;

/// <summary>Session-only review drafts. Never writes messages into launcher settings.</summary>
public static class GitCommitPreviewState
{
    private static readonly object Gate = new();
    private static readonly Dictionary<(string Root, string Branch, string Connection), GitCommitPreviewDraft> Drafts = [];

    public static GitCommitPreviewDraft? Read(string root, string branch, string connectionId)
    {
        lock (Gate) return Drafts.GetValueOrDefault(Key(root, branch, connectionId));
    }

    public static void Publish(string root, string branch, string connectionId, string message, string title,
        bool isCustom, bool includeAgentSummaries, IReadOnlyList<string> reviewedEntryIds, bool inclusionWasChosen = false)
    {
        lock (Gate)
        {
            var key = Key(root, branch, connectionId);
            // A long dashboard session cannot retain an unlimited number of abandoned scopes.
            if (!Drafts.ContainsKey(key) && Drafts.Count >= 100) Drafts.Remove(Drafts.Keys.First());
            Drafts[key] = new(message, title, isCustom, includeAgentSummaries, reviewedEntryIds.ToArray(), true, inclusionWasChosen);
        }
    }

    public static void Clear(string root, string branch, string connectionId)
    {
        lock (Gate) Drafts.Remove(Key(root, branch, connectionId));
    }

    public static void EndReview(string root, string branch, string connectionId)
    {
        lock (Gate)
        {
            var key = Key(root, branch, connectionId);
            if (Drafts.TryGetValue(key, out var draft)) Drafts[key] = draft with { IsReviewOpen = false };
        }
    }

    private static (string, string, string) Key(string root, string branch, string connectionId) =>
        (GitConnectionService.NormalizeRoot(root).ToUpperInvariant(), branch, connectionId);
}

public sealed record GitCommitPreviewDraft(string Message, string Title, bool IsCustom,
    bool IncludeAgentSummaries, IReadOnlyList<string> ReviewedEntryIds, bool IsReviewOpen, bool InclusionWasChosen);
