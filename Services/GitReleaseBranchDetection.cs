namespace FullStackLauncher.Services;

public sealed record GitReleaseBranchSuggestion(string? Branch, string Detail)
{
    public string? RemoteDefaultBranch { get; init; }
}

/// <summary>Suggests a release source without confirming or saving the choice.</summary>
public static class GitReleaseBranchDetection
{
    public static GitReleaseBranchSuggestion Detect(IEnumerable<string> branchNames, string? defaultBranch = null)
    {
        var names = branchNames.Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal).ToArray();
        var exactRelease = names.Where(name => string.Equals(name, "release", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (exactRelease.Length > 1)
            return new(null, "Multiple branches match release. Choose the release source explicitly.");
        if (exactRelease.Length == 1)
            return new(exactRelease[0], "Matched the release branch name. Confirm this source before continuing.");

        var releases = names.Where(name => name.StartsWith("release/", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("releases/", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (releases.Length > 1)
            return new(null, "Multiple release branches are available. Choose the release source explicitly.");
        if (releases.Length == 1)
            return new(releases[0], "Matched the only release branch name. Confirm this source before continuing.");

        if (defaultBranch is { Length: > 0 } && names.Contains(defaultBranch, StringComparer.Ordinal))
            return new(defaultBranch, "Matched the remote HEAD branch. Confirm it is the intended release source.");

        var conventional = names.Where(name => string.Equals(name, "main", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "master", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "trunk", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (conventional.Length > 1)
            return new(null, "Multiple default branch names are available. Choose the release source explicitly.");
        if (conventional.Length == 1)
            return new(conventional[0], "Matched a conventional default branch name. Confirm it is the intended release source.");

        return new(null, names.Length == 0
            ? "No remote branch names are available. Load the remote branches to choose a release source."
            : "No clear release source was detected. Choose the release source explicitly.");
    }
}
