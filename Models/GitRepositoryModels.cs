namespace FullStackLauncher.Models;

public sealed record GitRepositorySnapshot
{
    public string Folder { get; init; } = "";
    public string? RepositoryRoot { get; init; }
    public bool IsRepository { get; init; }
    public string Branch { get; init; } = "";
    public string HeadCommit { get; init; } = "";
    public bool IsDetached { get; init; }
    public bool IsUnborn { get; init; }
    public string? Upstream { get; init; }
    public int Ahead { get; init; }
    public int Behind { get; init; }
    public bool TrackingAvailable { get; init; }
    public string StateFingerprint { get; init; } = "";
    public IReadOnlyList<GitChange> Changes { get; init; } = [];
    public IReadOnlyList<GitBranchInfo> Branches { get; init; } = [];
    public IReadOnlyList<GitRemoteInfo> Remotes { get; init; } = [];
    public string? OperationState { get; init; }
}

public sealed record GitChange
{
    public string Path { get; init; } = "";
    public string? OriginalPath { get; init; }
    public string IndexStatus { get; init; } = "";
    public string WorkTreeStatus { get; init; } = "";
    public string Summary { get; init; } = "";
    public bool HasStaged { get; init; }
    public bool HasUnstaged { get; init; }
    public bool IsConflict { get; init; }

    // These labels describe the two versions Git keeps; they do no filesystem work.
    public string FileName => Path[(Path.LastIndexOf('/') + 1)..];
    public string FolderLabel => Path.LastIndexOf('/') is var slash && slash >= 0
        ? Path[..slash] : "Repository root";
    public string WorkingChangeLabel => IsConflict ? "Conflicting changes · needs attention"
        : DescribeChange(WorkTreeStatus);
    public string StagedChangeLabel => IsConflict ? "Conflicting changes · needs attention"
        : DescribeChange(IndexStatus);
    public string ChangeLabel => IsConflict ? "Conflicting changes · needs attention"
        : HasStaged && HasUnstaged ? $"Included: {StagedChangeLabel} · Later: {WorkingChangeLabel}"
        : HasStaged ? StagedChangeLabel : WorkingChangeLabel;
    public string InclusionLabel => IsConflict ? "Resolve this conflict with Git before saving a checkpoint."
        : HasStaged && HasUnstaged ? "An earlier version is included; further changes are still left out."
        : HasStaged ? "This version will be saved in the next checkpoint."
        : "These changes are not included in the next checkpoint yet.";

    private string DescribeChange(string status) => status switch
    {
        "?" => "New file · not tracked yet",
        "A" => "New file",
        "M" => "Edited",
        "D" => "Deleted · records its removal",
        "R" => OriginalPath is { Length: > 0 } ? $"Renamed from {OriginalPath}" : "Renamed",
        "C" => OriginalPath is { Length: > 0 } ? $"Copied from {OriginalPath}" : "Copied",
        "T" => "File type changed",
        "U" => "Conflicting changes · needs attention",
        "!" => "Ignored by Git",
        " " or "" => "No changes in this version",
        _ => "Change needs review"
    };
}

public sealed record GitBranchInfo
{
    public string Name { get; init; } = "";
    public string CommitId { get; init; } = "";
    public bool IsCurrent { get; init; }
    public bool IsRemote { get; init; }
    public string? Upstream { get; init; }
    public string DisplayLabel => $"{Name} · {(IsRemote ? "last fetched" : "local")}";
    public string LocationLabel => IsRemote ? "Last fetched" : "Local";
    public string CurrentLabel => IsCurrent ? "Current" : "";
}

public sealed record GitRemoteInfo
{
    public string Name { get; init; } = "";
    public string FetchUrl { get; init; } = "";
    public string PushUrl { get; init; } = "";
    public bool UrlCanCopy { get; init; }
}

/// <summary>Tracked working-tree changes from the selected remote's cached branch tip.</summary>
public sealed record GitRemoteComparison
{
    public string Remote { get; init; } = "";
    public string Branch { get; init; } = "";
    public bool Available { get; init; }
    public string? UnavailableReason { get; init; }
    public int Ahead { get; init; }
    public int Behind { get; init; }
    public int ChangedFiles { get; init; }
    public long AddedLines { get; init; }
    public long DeletedLines { get; init; }
    public int BinaryFiles { get; init; }
    public int UntrackedFiles { get; init; }
}

public sealed class GitCommitPushException(string message, bool commitCreated, Exception innerException)
    : InvalidOperationException(message, innerException)
{
    public bool CommitCreated { get; } = commitCreated;
}
