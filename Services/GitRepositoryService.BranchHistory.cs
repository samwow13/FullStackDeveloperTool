using System.Globalization;
using FullStackLauncher.Models;

namespace FullStackLauncher.Services;

public static partial class GitRepositoryService
{
    private const int BranchHistoryPageSize = 30;
    private const string BranchHistoryStaleMessage = "The current branch, its commit, or the selected branch changed. Refresh and select the branch again.";

    /// <summary>Reads only the captured selected branch's committed history, without comparing local HEAD. This never fetches.</summary>
    public static Task<GitBranchComparison> ReadBranchHistoryAsync(string root, GitBranchInfo selectedBranch,
        CancellationToken token = default) =>
        WithBranchHistoryReadAsync(root, token, async fullRoot =>
        {
            var history = new GitBranchComparison
            {
                RepositoryRoot = fullRoot,
                SelectedBranchName = selectedBranch.Name,
                SelectedCommitId = selectedBranch.CommitId,
                SelectedIsRemote = selectedBranch.IsRemote,
                SelectedBranchOnly = true
            };
            var unavailable = await ValidateBranchHistoryScopeAsync(history, token).ConfigureAwait(false);
            if (unavailable is not null) return history with { UnavailableReason = unavailable };

            var count = await BranchHistoryGitAsync(fullRoot,
                ["rev-list", "--count", history.SelectedCommitId, "--"], token).ConfigureAwait(false);
            EnsureSuccess(count);
            if (!TryReadBranchCommitCount(count.Output.Trim(), out var totalCommits) || totalCommits < 1)
                throw new InvalidOperationException("Git returned an invalid selected branch history count.");

            await RequireBranchHistoryScopeAsync(history, token).ConfigureAwait(false);
            return history with { Available = true, TotalCommits = totalCommits };
        });

    /// <summary>Reads committed differences against the exact selected tip displayed in the branch list. This never fetches.</summary>
    public static Task<GitBranchComparison> ReadBranchComparisonAsync(string root, string expectedCurrentBranch,
        string expectedHeadCommit, GitBranchInfo selectedBranch, CancellationToken token = default) =>
        WithBranchHistoryReadAsync(root, token, async fullRoot =>
        {
            var currentRef = await BranchHistoryGitAsync(fullRoot, ["symbolic-ref", "--quiet", "HEAD"], token).ConfigureAwait(false);
            if (currentRef.ExitCode is not (0 or 1)) ThrowCommandFailure(currentRef);
            var comparison = new GitBranchComparison
            {
                RepositoryRoot = fullRoot,
                CurrentBranch = expectedCurrentBranch,
                CurrentBranchRef = currentRef.ExitCode == 0 ? currentRef.Output.TrimEnd('\r', '\n') : "",
                LocalCommitId = expectedHeadCommit,
                SelectedBranchName = selectedBranch.Name,
                SelectedCommitId = selectedBranch.CommitId,
                SelectedIsRemote = selectedBranch.IsRemote
            };
            var unavailable = await ValidateBranchHistoryScopeAsync(comparison, token).ConfigureAwait(false);
            if (unavailable is not null) return comparison with { UnavailableReason = unavailable };

            var difference = await BranchHistoryGitAsync(fullRoot,
                ["rev-list", "--left-right", "--count", comparison.LocalCommitId + "..." + comparison.SelectedCommitId, "--"], token).ConfigureAwait(false);
            EnsureSuccess(difference);
            var counts = difference.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (counts.Length != 2 || !TryReadBranchCommitCount(counts[0], out var localOnly)
                || !TryReadBranchCommitCount(counts[1], out var selectedOnly))
                throw new InvalidOperationException("Git returned invalid branch comparison counts.");

            var total = await BranchHistoryGitAsync(fullRoot,
                ["rev-list", "--count", comparison.LocalCommitId, comparison.SelectedCommitId, "--"], token).ConfigureAwait(false);
            EnsureSuccess(total);
            if (!TryReadBranchCommitCount(total.Output.Trim(), out var totalCommits)
                || localOnly > totalCommits || selectedOnly > totalCommits - localOnly)
                throw new InvalidOperationException("Git returned an invalid branch history count.");

            await RequireBranchHistoryScopeAsync(comparison, token).ConfigureAwait(false);
            return comparison with
            {
                Available = true,
                LocalOnlyCommits = localOnly,
                SelectedOnlyCommits = selectedOnly,
                TotalCommits = totalCommits
            };
        });

    /// <summary>Reads one fixed-size page from the captured selected history or the union of two captured histories.</summary>
    public static Task<GitBranchHistoryPage> ReadBranchHistoryPageAsync(GitBranchComparison comparison, int pageIndex,
        CancellationToken token = default) =>
        WithBranchHistoryReadAsync(comparison.RepositoryRoot, token, async fullRoot =>
        {
            if (!comparison.Available || (!comparison.SelectedBranchOnly && !IsObjectId(comparison.LocalCommitId))
                || !IsObjectId(comparison.SelectedCommitId)
                || comparison.TotalCommits < 1)
                throw new InvalidOperationException(comparison.SelectedBranchOnly
                    ? "Select an available branch before opening commit history."
                    : "Select an available branch comparison before opening commit history.");
            var skip = (long)pageIndex * BranchHistoryPageSize;
            if (pageIndex < 0 || skip >= comparison.TotalCommits)
                throw new InvalidOperationException("That commit history page is unavailable. Reload the branch history.");

            await RequireBranchHistoryScopeAsync(comparison, token).ConfigureAwait(false);
            string[] tips = comparison.SelectedBranchOnly
                ? [comparison.SelectedCommitId]
                : [comparison.LocalCommitId, comparison.SelectedCommitId];
            var history = await BranchHistoryGitAsync(fullRoot,
                ["log", "--topo-order", "--no-decorate", "--no-show-signature", "--no-notes", "--no-patch", "--encoding=UTF-8", "-z",
                    "--format=%H%x00%ct%x00%an%x00%s", "--max-count=" + BranchHistoryPageSize.ToString(CultureInfo.InvariantCulture),
                    "--skip=" + skip.ToString(CultureInfo.InvariantCulture), .. tips, "--"], token).ConfigureAwait(false);
            EnsureSuccess(history);
            var commits = ParseBranchHistoryCommits(history.Output);
            if (commits.Count != Math.Min(BranchHistoryPageSize, comparison.TotalCommits - skip))
                throw new InvalidOperationException("Git returned an incomplete commit history page. Reload the branch history.");

            if (comparison.SelectedBranchOnly)
            {
                await RequireBranchHistoryScopeAsync(comparison, token).ConfigureAwait(false);
                return new GitBranchHistoryPage
                {
                    PageIndex = pageIndex, PageSize = BranchHistoryPageSize, TotalCommits = comparison.TotalCommits,
                    Commits = commits.Select(commit => commit with
                    {
                        SelectedIsRemote = comparison.SelectedIsRemote,
                        Membership = GitBranchCommitMembership.SelectedBranch
                    }).ToArray()
                };
            }

            // Annotating stdin disables name-rev's date cutoff and bounds output to this page.
            // Each exact ref can name only commits reachable from its captured, revalidated tip.
            var localMembership = await ReadBranchPageMembershipAsync(fullRoot, comparison.CurrentBranchRef,
                commits, token).ConfigureAwait(false);
            var selectedRef = BranchHistorySelectedRef(comparison);
            var selectedMembership = await ReadBranchPageMembershipAsync(fullRoot, selectedRef, commits, token).ConfigureAwait(false);
            var rows = new GitBranchHistoryCommit[commits.Count];
            for (var index = 0; index < commits.Count; index++)
            {
                if (!localMembership[index] && !selectedMembership[index])
                    throw new InvalidOperationException("A commit could not be classified against the captured branches. Reload the branch comparison.");
                rows[index] = commits[index] with
                {
                    SelectedIsRemote = comparison.SelectedIsRemote,
                    Membership = localMembership[index]
                        ? selectedMembership[index] ? GitBranchCommitMembership.BothBranches : GitBranchCommitMembership.LocalOnly
                        : GitBranchCommitMembership.SelectedBranchOnly
                };
            }
            await RequireBranchHistoryScopeAsync(comparison, token).ConfigureAwait(false);
            return new GitBranchHistoryPage
            {
                PageIndex = pageIndex, PageSize = BranchHistoryPageSize, TotalCommits = comparison.TotalCommits, Commits = rows
            };
        });

    private static Task<T> WithBranchHistoryReadAsync<T>(string root, CancellationToken token, Func<string, Task<T>> read) =>
        Task.Run(async () =>
        {
            var fullRoot = RequireFolder(root);
            var gate = RepositoryGates.GetOrAdd(fullRoot, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(token).ConfigureAwait(false);
            try { return await read(fullRoot).ConfigureAwait(false); }
            finally { gate.Release(); }
        }, token);

    private static async Task RequireBranchHistoryScopeAsync(GitBranchComparison comparison, CancellationToken token)
    {
        if (await ValidateBranchHistoryScopeAsync(comparison, token).ConfigureAwait(false) is { } unavailable)
            throw new InvalidOperationException(unavailable + " Refresh and select the branch again.");
    }

    private static async Task<string?> ValidateBranchHistoryScopeAsync(GitBranchComparison comparison, CancellationToken token)
    {
        var root = comparison.RepositoryRoot;
        var historyAction = comparison.SelectedBranchOnly ? "reading branch history" : "comparing branches";
        var historyLabel = comparison.SelectedBranchOnly ? "Commit history" : "Commit comparison";
        var top = await BranchHistoryGitAsync(root, ["rev-parse", "--show-toplevel"], token).ConfigureAwait(false);
        if (top.ExitCode != 0)
        {
            var metadata = await GitBranchReader.ReadAsync(root, token).ConfigureAwait(false);
            if (TryGetUntrustedRoot(top.Error, metadata.RepositoryPath) is { } untrustedRoot)
                throw new GitRepositoryTrustException(untrustedRoot);
            ThrowCommandFailure(top);
        }
        if (!string.Equals(RequireFolder(top.Output.TrimEnd('\r', '\n')), root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The selected folder no longer resolves to the displayed repository. Refresh before {historyAction}.");

        // A missing object in a partial clone can otherwise make a nominally local read fetch from its promisor remote.
        var partial = await BranchHistoryGitAsync(root,
            ["config", "--get-regexp", "^extensions\\.partialclone$|^remote\\..*\\.(promisor|partialclonefilter)$"], token).ConfigureAwait(false);
        if (partial.ExitCode is not (0 or 1)) ThrowCommandFailure(partial);
        foreach (var setting in partial.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = setting.IndexOf(' ');
            var key = separator < 0 ? setting.TrimEnd('\r') : setting[..separator];
            var value = separator < 0 ? "" : setting[(separator + 1)..].Trim();
            if (key.Equals("extensions.partialclone", StringComparison.OrdinalIgnoreCase)
                || key.EndsWith(".partialclonefilter", StringComparison.OrdinalIgnoreCase)
                || value.Length == 0 || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase) || value.Equals("on", StringComparison.OrdinalIgnoreCase) || value == "1")
                return $"{historyLabel} is unavailable for partial clones because local history may require a remote download.";
            if (!value.Equals("false", StringComparison.OrdinalIgnoreCase) && !value.Equals("no", StringComparison.OrdinalIgnoreCase)
                && !value.Equals("off", StringComparison.OrdinalIgnoreCase) && value != "0")
                return $"Partial clone settings could not be verified. {historyLabel} is unavailable.";
        }
        var shallow = await BranchHistoryGitAsync(root, ["rev-parse", "--is-shallow-repository"], token).ConfigureAwait(false);
        EnsureSuccess(shallow);
        if (shallow.Output.Trim() == "true")
            return $"This checkout has shallow history. Complete its history with Git before {historyAction}.";
        if (shallow.Output.Trim() != "false")
            throw new InvalidOperationException("Git could not verify whether the committed history is complete.");

        if (comparison.SelectedBranchOnly)
            return await ValidateSelectedBranchHistoryTipAsync(comparison, token).ConfigureAwait(false);

        var symbolic = await BranchHistoryGitAsync(root, ["symbolic-ref", "--quiet", "--short", "HEAD"], token).ConfigureAwait(false);
        if (symbolic.ExitCode == 1) return "Check out a local branch before comparing committed history.";
        EnsureSuccess(symbolic);
        if (symbolic.Output.TrimEnd('\r', '\n') != comparison.CurrentBranch)
            throw new InvalidOperationException(BranchHistoryStaleMessage);
        var currentRef = await BranchHistoryGitAsync(root, ["symbolic-ref", "--quiet", "HEAD"], token).ConfigureAwait(false);
        if (currentRef.ExitCode != 0 || currentRef.Output.TrimEnd('\r', '\n') != comparison.CurrentBranchRef)
            throw new InvalidOperationException(BranchHistoryStaleMessage);
        if (!comparison.CurrentBranchRef.StartsWith("refs/heads/", StringComparison.Ordinal))
            return "Check out a local branch before comparing committed history.";

        var head = await BranchHistoryGitAsync(root, ["rev-parse", "--verify", "HEAD^{commit}"], token).ConfigureAwait(false);
        if (head.ExitCode != 0)
        {
            var branch = await BranchHistoryGitAsync(root, ["show-ref", "--verify", "--quiet", comparison.CurrentBranchRef], token).ConfigureAwait(false);
            if (branch.ExitCode == 1 && comparison.LocalCommitId.Length == 0)
                return "Create the first commit before comparing branch history.";
            ThrowCommandFailure(head);
        }
        if (!IsObjectId(comparison.LocalCommitId) || head.Output.Trim() != comparison.LocalCommitId
            || !IsObjectId(comparison.SelectedCommitId))
            throw new InvalidOperationException(BranchHistoryStaleMessage);

        return await ValidateSelectedBranchHistoryTipAsync(comparison, token).ConfigureAwait(false);
    }

    private static async Task<string?> ValidateSelectedBranchHistoryTipAsync(GitBranchComparison comparison, CancellationToken token)
    {
        var staleMessage = comparison.SelectedBranchOnly
            ? "The selected branch or its commit changed. Refresh and select the branch again."
            : BranchHistoryStaleMessage;
        if (!IsObjectId(comparison.SelectedCommitId))
            throw new InvalidOperationException(staleMessage);
        var root = comparison.RepositoryRoot;
        var selectedRef = BranchHistorySelectedRef(comparison);
        EnsureSuccess(await BranchHistoryGitAsync(root, ["check-ref-format", selectedRef], token).ConfigureAwait(false));
        var selectedAlias = await BranchHistoryGitAsync(root, ["symbolic-ref", "--quiet", selectedRef], token).ConfigureAwait(false);
        if (selectedAlias.ExitCode == 0) return "Select a branch rather than a symbolic branch alias.";
        if (selectedAlias.ExitCode != 1) ThrowCommandFailure(selectedAlias);
        var selectedTip = await BranchHistoryGitAsync(root, ["show-ref", "--verify", "--hash", selectedRef], token).ConfigureAwait(false);
        if (selectedTip.ExitCode != 0 || selectedTip.Output.Trim() != comparison.SelectedCommitId)
            throw new InvalidOperationException(staleMessage);
        return null;
    }

    private static string BranchHistorySelectedRef(GitBranchComparison comparison) =>
        (comparison.SelectedIsRemote ? "refs/remotes/" : "refs/heads/") + comparison.SelectedBranchName;

    private static bool TryReadBranchCommitCount(string value, out long count) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out count) && count >= 0;

    private static List<GitBranchHistoryCommit> ParseBranchHistoryCommits(string output)
    {
        if (output.Length == 0) return [];
        if (output[^1] != '\0') throw new InvalidOperationException("Git returned an incomplete commit history record.");
        var fields = output.Split('\0');
        if ((fields.Length - 1) % 4 != 0 || (fields.Length - 1) / 4 > BranchHistoryPageSize)
            throw new InvalidOperationException("Git returned an unsupported commit history record.");
        var commits = new List<GitBranchHistoryCommit>((fields.Length - 1) / 4);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < fields.Length - 1; index += 4)
        {
            if (!IsObjectId(fields[index]) || !seen.Add(fields[index])
                || !long.TryParse(fields[index + 1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var timestamp))
                throw new InvalidOperationException("Git returned invalid commit history metadata.");
            DateTimeOffset committedAt;
            try { committedAt = DateTimeOffset.FromUnixTimeSeconds(timestamp); }
            catch (ArgumentOutOfRangeException)
            { throw new InvalidOperationException("A commit timestamp is outside the supported date range."); }
            commits.Add(new GitBranchHistoryCommit
            {
                CommitId = fields[index], CommittedAt = committedAt,
                Author = Sanitize(fields[index + 2]), Subject = Sanitize(fields[index + 3])
            });
        }
        return commits;
    }

    private static async Task<bool[]> ReadBranchPageMembershipAsync(string root, string exactRef,
        IReadOnlyList<GitBranchHistoryCommit> commits, CancellationToken token)
    {
        var input = string.Join('\n', commits.Select(commit => commit.CommitId)) + "\n";
        // name-rev also matches ref suffixes. Exclude longer paths ending with this exact ref.
        // Keep the hash annotation so a hexadecimal branch name cannot resemble an unnamed commit.
        var names = await BranchHistoryGitAsync(root,
            ["name-rev", "--annotate-stdin", "--refs=" + exactRef, "--exclude=?*" + exactRef], token, input).ConfigureAwait(false);
        EnsureSuccess(names);
        if (!string.IsNullOrWhiteSpace(names.Error))
            throw new InvalidOperationException("Git could not verify complete commit reachability. Reload the branch comparison.");
        var lines = names.Output.Split('\n');
        if (lines.Length != commits.Count + 1 || lines[^1].Length != 0)
            throw new InvalidOperationException("Git returned an incomplete commit membership list.");
        var membership = new bool[commits.Count];
        var shortName = exactRef.StartsWith("refs/heads/", StringComparison.Ordinal) ? exactRef[11..] : exactRef[5..];
        var qualifiedName = exactRef[5..];
        for (var index = 0; index < commits.Count; index++)
        {
            var line = lines[index].TrimEnd('\r');
            if (line == commits[index].CommitId) continue;
            var prefix = commits[index].CommitId + " (";
            if (!line.StartsWith(prefix, StringComparison.Ordinal) || !line.EndsWith(')'))
                throw new InvalidOperationException("Git returned an invalid commit membership annotation.");
            var name = line[prefix.Length..^1];
            if (!MatchesBranchHistoryName(name, shortName) && !MatchesBranchHistoryName(name, qualifiedName)
                && !MatchesBranchHistoryName(name, exactRef))
                throw new InvalidOperationException("Git returned an unexpected branch name while classifying commits.");
            membership[index] = true;
        }
        return membership;
    }

    private static bool MatchesBranchHistoryName(string name, string branchName) =>
        name == branchName || name.StartsWith(branchName + "~", StringComparison.Ordinal)
            || name.StartsWith(branchName + "^", StringComparison.Ordinal);

    private static Task<CommandResult> BranchHistoryGitAsync(string root, IReadOnlyList<string> arguments, CancellationToken token,
        string? input = null) =>
        GitAsync(root, ["--no-replace-objects", "-c", "protocol.https.allow=never", "-c", "protocol.ssh.allow=never",
            "-c", "protocol.http.allow=never", "-c", "protocol.git.allow=never", "-c", "protocol.file.allow=never", "-c", "protocol.ext.allow=never", .. arguments],
            token, standardInput: input);
}
