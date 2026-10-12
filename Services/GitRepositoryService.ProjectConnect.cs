using System.IO;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using FullStackLauncher.Models;
using Microsoft.Win32.SafeHandles;

namespace FullStackLauncher.Services;

public static partial class GitRepositoryService
{
    private const int ConnectEntryLimit = 50_000;
    private const int ConnectDirectoryLimit = 4_000;
    private const long ConnectByteLimit = 4L * 1024 * 1024 * 1024;
    private const long ConnectFileByteLimit = 512L * 1024 * 1024;
    private static readonly string[] ConnectExcludedDirectories =
    [
        "node_modules", "bin", "obj", ".vs", ".idea", ".venv", "venv", "__pycache__", ".pytest_cache",
        ".mypy_cache", ".angular", ".next", ".nuxt", ".cache", "coverage", "TestResults", "dist", "build",
        "artifacts", "publish", "portable", "api-runtime", "monitor-runtime", "api-secrets", "database-connections",
        "azure-devops-tokens", "codex-agent-chats", "codex-agent-keep-awake", ".ssh", ".aws", ".azure", ".svn", ".hg"
    ];
    private static readonly string[] ConnectExcludedFiles =
    [
        ".env", ".env.*", "secrets.json*", "appsettings.Local.json*", "appsettings.*.Local.json*",
        "local.settings.json*", "*.settings.local.json*", "*.local.settings.json*", "*.local.json*",
        "*.pfx", "*.p12", "*.key", "*.pem", "id_rsa*", "id_ed25519*", ".npmrc", ".pypirc", ".netrc",
        "*.user", "*.suo", "*.log", "*.db", "*.db-*", "*.sqlite", "*.sqlite-*", "*.sqlite3", "*.sqlite3-*",
        "launcher*.json*", "*.settings.json*", "project-tasks.json*", "*.project-tasks.json*",
        "codex-reply-inbox.json*", "*.codex-reply-inbox.json*", "codex-agent-prompts.json*", "*.codex-agent-prompts.json*",
        "window-snip-targets.json*", "*.window-snip-targets.json*", "*.json.bak", "*.json.*.bak", "*.json.*.tmp"
    ];

    /// <summary>Reads the remote and inventories local files without changing the source or creating the clone destination.</summary>
    public static Task<GitProjectConnectReview> PrepareProjectConnectAsync(GitProjectConnectRequest request,
        CancellationToken token = default) => Task.Run(async () =>
    {
        ArgumentNullException.ThrowIfNull(request);
        var source = RequireFolder(request.SourceFolder);
        var url = GitConnectionService.ValidateUrl(request.Provider, request.CloneUrl);
        if (!Enum.IsDefined(request.Mode)) throw new InvalidOperationException("Choose how to connect this project.");
        using var sourceDirectory = OpenCloneDirectory(source, out var sourceIdentity);
        await RequireNonRepositoryCloneFolderAsync(source, token).ConfigureAwait(false);
        await RequireCloneRemoteEnvironmentAsync(source, url, token).ConfigureAwait(false);

        string repository;
        string project;
        string clone;
        string parent;
        string? subfolder = null;
        if (request.Mode == GitProjectConnectMode.EmptyRemoteInPlace)
        {
            await ValidateBranchAsync(source, request.Branch, token).ConfigureAwait(false);
            repository = project = source;
            parent = Directory.GetParent(source)?.FullName
                ?? throw new InvalidOperationException("A drive root cannot be connected as a project. Choose a project folder.");
            clone = RequireWorkspaceChildPath(parent, ".launcher-connect-" + Guid.NewGuid().ToString("N"), "temporary clone");
        }
        else
        {
            repository = RequireNewConnectDestination(request.DestinationFolder);
            parent = RequireFolder(Path.GetDirectoryName(repository)!);
            if (ConnectContainsPath(source, repository) || ConnectContainsPath(repository, source))
                throw new InvalidOperationException("Choose a new clone folder outside the original project, with no folder overlap.");
            project = RequireConnectSubfolder(repository, request.RepositorySubfolder, out subfolder);
            clone = repository;
        }
        using var parentDirectory = OpenCloneDirectory(parent, out var parentIdentity);
        await RequireNonRepositoryCloneFolderAsync(parent, token).ConfigureAwait(false);
        RequireMissingRepositoryFolder(clone);
        var remote = await ReadConnectRemoteAsync(source, url, token).ConfigureAwait(false);
        string? startingBranch = null;
        string? startingCommit = null;
        if (request.Mode == GitProjectConnectMode.EmptyRemoteInPlace)
        {
            if (!remote.IsEmpty)
                throw new InvalidOperationException("This remote already contains Git history. Choose 'Add this project to an existing repository' to preserve its existing files and branches.");
        }
        else
        {
            if (remote.IsEmpty)
                throw new InvalidOperationException("This remote is empty. Choose 'Use an empty remote' to keep this project's current location.");
            startingBranch = string.IsNullOrWhiteSpace(request.StartingBranch) ? remote.DefaultBranch : request.StartingBranch.Trim();
            if (string.IsNullOrEmpty(startingBranch) || !remote.Branches.TryGetValue(startingBranch, out startingCommit))
                throw new InvalidOperationException("The starting branch is unavailable. Enter an existing remote branch; an empty value requires a valid remote default branch.");
            await ValidateBranchAsync(source, startingBranch, token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(request.FeatureBranch))
            {
                await ValidateWorkspaceBranchesAsync(source, startingBranch, request.FeatureBranch.Trim(), token).ConfigureAwait(false);
                if (remote.Branches.Keys.Any(branch => CloneBranchNamesConflict(branch, request.FeatureBranch.Trim())))
                    throw new InvalidOperationException("The new branch conflicts with an existing remote branch. Choose another name.");
            }
        }
        var normalized = request with
        {
            SourceFolder = source, CloneUrl = url, DestinationFolder = repository,
            RepositorySubfolder = subfolder, StartingBranch = startingBranch,
            FeatureBranch = string.IsNullOrWhiteSpace(request.FeatureBranch) ? null : request.FeatureBranch.Trim()
        };
        var inventory = await InventoryConnectSourceAsync(source, token).ConfigureAwait(false);
        if (inventory.Files.Count == 0)
            throw new InvalidOperationException("No source files remain after Git ignore rules and local-file exclusions. Review the source folder and ignore rules.");
        RequireMissingRepositoryFolder(clone);
        return new GitProjectConnectReview(normalized, repository, project, sourceIdentity, parent, parentIdentity,
            clone, startingCommit, inventory.Files, inventory.Excluded, inventory.Directories);
    }, token);

    /// <summary>Connects the reviewed project locally. Never stages, commits, pushes, overwrites, or removes source files.</summary>
    public static Task<GitProjectConnectResult> ConnectProjectAsync(GitProjectConnectReview review,
        IProgress<string>? progress = null, CancellationToken token = default) => Task.Run(async () =>
    {
        ArgumentNullException.ThrowIfNull(review);
        var source = RequireFolder(review.SourceFolder);
        var gate = RepositoryGates.GetOrAdd(source, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token).ConfigureAwait(false);
        var cloneCreated = false;
        var attached = false;
        try
        {
            await using var sourceLease = await AcquireGitSetupOperationLeaseAsync(source, token).ConfigureAwait(false);
            await using var parentLease = await AcquireGitSetupOperationLeaseAsync(review.ParentFolder, token).ConfigureAwait(false);
            using var sourceDirectory = OpenCloneDirectory(source, out var sourceIdentity);
            using var parentDirectory = OpenCloneDirectory(review.ParentFolder, out var parentIdentity);
            if (sourceIdentity != review.SourceIdentity || parentIdentity != review.ParentIdentity)
                throw new InvalidOperationException("A reviewed folder changed. Review the project connection again.");
            await RequireNonRepositoryCloneFolderAsync(source, token).ConfigureAwait(false);
            await RequireNonRepositoryCloneFolderAsync(review.ParentFolder, token).ConfigureAwait(false);
            var url = GitConnectionService.ValidateUrl(review.Request.Provider, review.Request.CloneUrl);
            if (url != review.Request.CloneUrl) throw new InvalidOperationException("The reviewed URL changed. Review the connection again.");
            await RequireCloneRemoteEnvironmentAsync(source, url, token).ConfigureAwait(false);
            RequireMissingRepositoryFolder(review.CloneFolder);
            progress?.Report("Checking the reviewed source files and remote branch…");
            RequireSameConnectInventory(review, await InventoryConnectSourceAsync(source, token).ConfigureAwait(false));
            RequireSameConnectRemote(review, await ReadConnectRemoteAsync(source, url, token).ConfigureAwait(false));
            token.ThrowIfCancellationRequested();
            CreateNewWorkspaceDirectory(review.CloneFolder);
            cloneCreated = true;
            using var cloneDirectory = OpenCloneDirectory(review.CloneFolder, out var cloneIdentity);
            var cloneReview = new GitCloneReview(review.CloneFolder, review.Request.Provider, url, cloneIdentity,
                review.Request.Mode == GitProjectConnectMode.CopyIntoRepository ? review.StartingBranch : null);
            progress?.Report("Cloning the reviewed repository…");
            var cloneMessage = await CloneAsync(cloneReview, token).ConfigureAwait(false);
            var cloneSnapshot = await ReadCoreAsync(review.CloneFolder, token).ConfigureAwait(false);
            RequireExactRoot(review.CloneFolder, cloneSnapshot);
            await RequireConnectOriginAsync(review.CloneFolder, url, token).ConfigureAwait(false);
            string branch;
            if (review.Request.Mode == GitProjectConnectMode.EmptyRemoteInPlace)
            {
                progress?.Report("Verifying the empty repository before connecting this folder…");
                await RequireEmptyConnectCloneAsync(review.CloneFolder, cloneSnapshot, token).ConfigureAwait(false);
                await using (var repositoryLease = await AcquireRepositoryOperationLeaseAsync(review.CloneFolder, token).ConfigureAwait(false))
                {
                    await ValidateBranchAsync(review.CloneFolder, review.Request.Branch, token).ConfigureAwait(false);
                    EnsureSuccess(await GitAsync(review.CloneFolder,
                        ["symbolic-ref", "HEAD", "refs/heads/" + review.Request.Branch], token).ConfigureAwait(false));
                    EnsureSuccess(await GitAsync(review.CloneFolder,
                        ["config", "--local", "--replace-all", "branch." + review.Request.Branch + ".remote", "origin"], token).ConfigureAwait(false));
                    EnsureSuccess(await GitAsync(review.CloneFolder,
                        ["config", "--local", "--replace-all", "branch." + review.Request.Branch + ".merge", "refs/heads/" + review.Request.Branch], token).ConfigureAwait(false));
                    WriteConnectExclusions(review.CloneFolder);
                    var ready = await ReadCoreAsync(review.CloneFolder, token).ConfigureAwait(false);
                    await RequireEmptyConnectCloneAsync(review.CloneFolder, ready, token).ConfigureAwait(false);
                    if (ready.Branch != review.Request.Branch)
                        throw new InvalidOperationException("The initial branch could not be verified. Inspect the temporary clone.");
                }
                // The repository lease is closed before moving metadata; never copy open Git lock handles.
                RequireSameConnectInventory(review, await InventoryConnectSourceAsync(source, token).ConfigureAwait(false));
                RequireSameConnectRemote(review, await ReadConnectRemoteAsync(source, url, token).ConfigureAwait(false));
                await RequireNonRepositoryCloneFolderAsync(source, token).ConfigureAwait(false);
                RequireMissingRepositoryFolder(Path.Combine(source, ".git"));
                token.ThrowIfCancellationRequested();
                Directory.Move(Path.Combine(review.CloneFolder, ".git"), Path.Combine(source, ".git"));
                attached = true;
                // No cancellation point between confirmed metadata move and its reported local outcome.
                branch = review.Request.Branch;
                var message = "Connected the existing folder to the empty repository. Local branch '" + branch
                    + "' is ready for its first commit. No files were staged, committed, or pushed. Default local-file exclusions were added to .git/info/exclude; existing .gitignore rules retain their normal Git precedence.\n"
                    + cloneMessage;
                try
                {
                    var final = await ReadCoreAsync(source, token).ConfigureAwait(false);
                    RequireExactRoot(source, final);
                    if (!final.IsUnborn || final.Branch != branch)
                        throw new InvalidOperationException("The attached branch state changed. Inspect the repository before continuing.");
                    await RequireConnectOriginAsync(source, url, token).ConfigureAwait(false);
                }
                catch (GitCommandExitUnconfirmedException exception) { throw exception.WithContext(message); }
                catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or OperationCanceledException)
                { message += "\nThe connection was created, but final status could not be verified. Refresh Git before another action.\n" + Sanitize(exception.Message); }
                // Delete only the empty staging directory, after releasing its identity handle.
                cloneDirectory.Dispose();
                try { Directory.Delete(review.CloneFolder); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                { message += "\nThe temporary clone folder was retained: " + review.CloneFolder; }
                return new GitProjectConnectResult(source, source, source, branch, message);
            }

            await using (var repositoryLease = await AcquireRepositoryOperationLeaseAsync(review.CloneFolder, token).ConfigureAwait(false))
            {
                RequireNoOperation(cloneSnapshot);
                RequireClean(cloneSnapshot);
                if (cloneSnapshot.IsUnborn || cloneSnapshot.IsDetached || cloneSnapshot.Branch != review.StartingBranch
                    || cloneSnapshot.HeadCommit != review.StartingCommit)
                    throw new InvalidOperationException("The remote starting branch changed after review. The downloaded clone was retained; review it before continuing.");
                var remoteTip = await GitAsync(review.CloneFolder,
                    ["show-ref", "--verify", "--hash", "refs/remotes/origin/" + review.StartingBranch], token).ConfigureAwait(false);
                EnsureSuccess(remoteTip);
                if (remoteTip.Output.Trim() != review.StartingCommit)
                    throw new InvalidOperationException("The downloaded remote branch changed after review. No source files were copied.");
                if (review.Request.FeatureBranch is { } feature)
                    await CreateCloneFeatureBranchAsync(review.CloneFolder, cloneSnapshot, review.StartingBranch!, feature,
                        sourceLease.Name, token).ConfigureAwait(false);
                branch = review.Request.FeatureBranch ?? review.StartingBranch!;
                var beforeCopy = await ReadCoreAsync(review.CloneFolder, token).ConfigureAwait(false);
                RequireNoOperation(beforeCopy);
                RequireClean(beforeCopy);
                RequireSameConnectInventory(review, await InventoryConnectSourceAsync(source, token).ConfigureAwait(false));
                RequireMissingRepositoryFolder(review.ProjectFolder);
                await RequireConnectFilesVisibleAsync(review, token).ConfigureAwait(false);
                progress?.Report("Copying reviewed source files into the new project folder…");
                await CopyConnectSourceAsync(review, token).ConfigureAwait(false);
                RequireSameConnectInventory(review, await InventoryConnectSourceAsync(source, token).ConfigureAwait(false));
                await RequireConnectFilesVisibleAsync(review, token).ConfigureAwait(false);
                var final = await ReadCoreAsync(review.CloneFolder, token).ConfigureAwait(false);
                RequireExactRoot(review.CloneFolder, final);
                RequireNoOperation(final);
                if (final.Branch != branch || final.HeadCommit != review.StartingCommit)
                    throw new InvalidOperationException("The clone branch changed during the copy. Inspect the retained clone before continuing.");
                await RequireConnectOriginAsync(review.CloneFolder, url, token).ConfigureAwait(false);
            }
            return new GitProjectConnectResult(review.RepositoryFolder, review.ProjectFolder, source, branch,
                "Repository cloned and project files copied to the reviewed new subfolder. The original project is unchanged. "
                + "No files were staged, committed, or pushed. Excluded local settings, credentials, and generated files remain in the original folder.\n" + cloneMessage);
        }
        catch (GitCommandExitUnconfirmedException exception)
        { throw exception.WithContext(ConnectFailureContext(review, cloneCreated, attached)); }
        catch (OperationCanceledException exception)
        { throw new OperationCanceledException(ConnectFailureContext(review, cloneCreated, attached) + "\n" + Sanitize(exception.Message), exception, exception.CancellationToken); }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        { throw new InvalidOperationException(ConnectFailureContext(review, cloneCreated, attached) + "\n" + Sanitize(exception.Message), exception); }
        finally { gate.Release(); }
    }, token);

    private static string ConnectFailureContext(GitProjectConnectReview review, bool cloneCreated, bool attached) => attached
        ? "Git metadata was connected to the original folder. Inspect its current state before another action: " + review.SourceFolder
        : cloneCreated ? "Connection did not complete. The original project remains unchanged. The clone and any copied files were retained for inspection; no cleanup or rollback was attempted: " + review.CloneFolder
        : "Connection did not start. Review the source and destination again.";

    private sealed record ConnectRemote(bool IsEmpty, string? DefaultBranch, IReadOnlyDictionary<string, string> Branches);

    private static async Task<ConnectRemote> ReadConnectRemoteAsync(string folder, string url, CancellationToken token)
    {
        var result = await GitAsync(folder,
            ["-c", "http.followRedirects=false", "-c", "http." + url + ".sslVerify=true", "ls-remote", "--symref", "--", url],
            token, network: true, interactive: true, strictSsh: true).ConfigureAwait(false);
        EnsureSuccess(result);
        var branches = new Dictionary<string, string>(StringComparer.Ordinal);
        string? defaultBranch = null;
        var hasRefs = false;
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = line.TrimEnd('\r').Split('\t');
            if (pair.Length != 2) throw new InvalidOperationException("The remote returned an unreadable ref list. Inspect it with Git.");
            if (pair[0].StartsWith("ref: ", StringComparison.Ordinal))
            {
                if (pair[1] == "HEAD" && pair[0].StartsWith("ref: refs/heads/", StringComparison.Ordinal))
                    defaultBranch = pair[0]["ref: refs/heads/".Length..];
                continue;
            }
            if (!IsObjectId(pair[0])) throw new InvalidOperationException("The remote returned an invalid branch identity.");
            hasRefs = true;
            if (pair[1].StartsWith("refs/heads/", StringComparison.Ordinal)
                && !branches.TryAdd(pair[1]["refs/heads/".Length..], pair[0]))
                throw new InvalidOperationException("The remote returned ambiguous branch identities.");
        }
        return new ConnectRemote(!hasRefs, defaultBranch, branches);
    }

    private static void RequireSameConnectRemote(GitProjectConnectReview review, ConnectRemote remote)
    {
        if (review.Request.Mode == GitProjectConnectMode.EmptyRemoteInPlace)
        {
            if (!remote.IsEmpty) throw new InvalidOperationException("The remote now contains history. Review a new connection before continuing.");
        }
        else if (review.StartingBranch is null || !remote.Branches.TryGetValue(review.StartingBranch, out var tip)
            || tip != review.StartingCommit || review.Request.FeatureBranch is { } feature
            && remote.Branches.Keys.Any(branch => CloneBranchNamesConflict(branch, feature)))
            throw new InvalidOperationException("The remote branch changed after review. Review the connection again before cloning.");
    }

    private static async Task RequireConnectOriginAsync(string root, string url, CancellationToken token)
    {
        if (await RequireSingleRemoteAsync(root, "origin", false, token).ConfigureAwait(false) != url
            || await RequireSingleRemoteAsync(root, "origin", true, token).ConfigureAwait(false) != url)
            throw new InvalidOperationException("The repository remote no longer matches the reviewed URL.");
    }

    private static async Task RequireEmptyConnectCloneAsync(string root, GitRepositorySnapshot snapshot, CancellationToken token)
    {
        RequireExactRoot(root, snapshot);
        RequireNoOperation(snapshot);
        RequireClean(snapshot);
        var refs = await GitAsync(root, ["for-each-ref", "--format=%(refname)"], token).ConfigureAwait(false);
        var index = await GitAsync(root, ["ls-files", "--stage", "-z"], token).ConfigureAwait(false);
        EnsureSuccess(refs);
        EnsureSuccess(index);
        if (!snapshot.IsUnborn || snapshot.IsDetached || snapshot.HeadCommit.Length != 0 || refs.Output.Length != 0
            || index.Output.Length != 0 || Directory.EnumerateFileSystemEntries(root).Any(path => Path.GetFileName(path) != ".git"))
            throw new InvalidOperationException("The downloaded repository is not empty. Its clone was retained; no original files were changed.");
        if (!Directory.Exists(Path.Combine(root, ".git")) || new DirectoryInfo(Path.Combine(root, ".git")).LinkTarget is not null)
            throw new InvalidOperationException("The clone metadata is not a regular Git directory. It cannot be attached to the project.");
    }

    private static string RequireNewConnectDestination(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || folder.Any(char.IsControl) || !Path.IsPathFullyQualified(folder))
            throw new InvalidOperationException("Choose an absolute path for a new local clone folder.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var parent = Path.GetDirectoryName(full);
        if (parent is null) throw new InvalidOperationException("Choose a new folder inside an existing parent folder.");
        var validated = RequireWorkspaceChildPath(RequireFolder(parent), Path.GetFileName(full), "repository");
        RequireMissingRepositoryFolder(validated);
        return validated;
    }

    private static string RequireConnectSubfolder(string root, string? relative, out string normalized)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Any(char.IsControl))
            throw new InvalidOperationException("Enter a new project subfolder inside the repository, such as Utilities/MyProject.");
        var segments = relative.Replace('\\', '/').Split('/');
        if (segments.Length > 32) throw new InvalidOperationException("The project subfolder is too deeply nested.");
        var result = root;
        foreach (var segment in segments) result = RequireWorkspaceChildPath(result, segment, "project");
        normalized = string.Join('/', segments);
        return result;
    }

    private static bool ConnectContainsPath(string parent, string path) => path.Equals(parent, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private sealed record ConnectInventory(IReadOnlyList<GitProjectConnectFile> Files, IReadOnlyList<string> Excluded,
        IReadOnlyList<GitProjectConnectDirectory> Directories);

    private static async Task<ConnectInventory> InventoryConnectSourceAsync(string root, CancellationToken token)
    {
        var candidates = new List<(string Relative, bool Directory)>();
        var directories = new List<GitProjectConnectDirectory>();
        var excluded = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<(string Folder, int Depth)>();
        pending.Push((root, 0));
        while (pending.TryPop(out var current))
        {
            token.ThrowIfCancellationRequested();
            if (current.Depth > 64 || directories.Count >= ConnectDirectoryLimit)
                throw new InvalidOperationException("The source exceeds the guided connection directory limit. Reduce the project scope or use Git directly.");
            using var handle = OpenCloneDirectory(current.Folder, out var identity);
            directories.Add(new GitProjectConnectDirectory(Path.GetRelativePath(root, current.Folder).Replace('\\', '/'), identity));
            foreach (var entry in new DirectoryInfo(current.Folder).EnumerateFileSystemInfos())
            {
                token.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(root, entry.FullName).Replace('\\', '/');
                if (candidates.Count + excluded.Count >= ConnectEntryLimit)
                    throw new InvalidOperationException("The source exceeds 50,000 entries. Reduce its scope or use Git directly.");
                if (!visited.Add(relative)) throw new InvalidOperationException("The source contains filenames that collide on Windows: " + Sanitize(relative));
                if (entry.Name.Equals(".git", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The source contains nested Git metadata. Connect that repository separately: " + Sanitize(relative));
                if (entry.LinkTarget is not null)
                    throw new InvalidOperationException("The source contains a symbolic link or junction. Guided copying cannot follow it: " + Sanitize(relative));
                // Validate every path component using the same conservative Windows rules as destinations.
                _ = RequireWorkspaceChildPath(current.Folder, entry.Name, "source");
                var isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
                if (isDirectory ? ConnectExcludedDirectories.Contains(entry.Name, StringComparer.OrdinalIgnoreCase)
                    : ConnectExcludedFiles.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, entry.Name, ignoreCase: true)))
                {
                    excluded.Add(relative + (isDirectory ? "/" : "") + " — local settings, credentials, or generated files");
                    continue;
                }
                candidates.Add((relative, isDirectory));
                if (isDirectory) pending.Push((entry.FullName, current.Depth + 1));
            }
        }
        var ignored = await ReadConnectIgnoredAsync(root, candidates, token).ConfigureAwait(false);
        var files = new List<GitProjectConnectFile>();
        long bytes = 0;
        foreach (var candidate in candidates.OrderBy(item => item.Relative, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if (ignored.Contains(candidate.Relative))
            {
                excluded.Add(candidate.Relative + (candidate.Directory ? "/" : "") + " — Git ignore rules");
                continue;
            }
            if (candidate.Directory) continue;
            var path = Path.Combine(root, candidate.Relative.Replace('/', Path.DirectorySeparatorChar));
            using var parent = OpenCloneDirectory(Path.GetDirectoryName(path)!, out _);
            var info = new FileInfo(path);
            if (info.LinkTarget is not null) throw new InvalidOperationException("A source file became a link. Review the project again.");
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            RequireConnectFileHandle(stream, path);
            var length = stream.Length;
            bytes = checked(bytes + length);
            if (length > ConnectFileByteLimit || bytes > ConnectByteLimit)
                throw new InvalidOperationException("The source exceeds the guided copy limit (512 MiB per file or 4 GiB total). Exclude generated files or use Git directly.");
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
            info.Refresh();
            if (info.Length != length) throw new InvalidOperationException("A source file changed during review. Review the project again.");
            files.Add(new GitProjectConnectFile(candidate.Relative, length, info.LastWriteTimeUtc.Ticks, hash));
        }
        return new ConnectInventory(files, excluded.OrderBy(path => path, StringComparer.Ordinal).ToArray(),
            directories.Where(directory => directory.RelativePath == "." || !ignored.Contains(directory.RelativePath))
                .OrderBy(directory => directory.RelativePath, StringComparer.Ordinal).ToArray());
    }

    private static async Task<HashSet<string>> ReadConnectIgnoredAsync(string root,
        IReadOnlyList<(string Relative, bool Directory)> candidates, CancellationToken token)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local)) throw new InvalidOperationException("The local application data folder is unavailable.");
        var container = Path.Combine(local, "FullStackLauncher", "git-setup", "inventory");
        Directory.CreateDirectory(container);
        RequireUnlinkedCloneAncestry(container);
        var scratch = Path.Combine(container, Guid.NewGuid().ToString("N"));
        CreateNewWorkspaceDirectory(scratch);
        using var scratchHandle = OpenCloneDirectory(scratch, out _);
        var uncertain = false;
        try
        {
            // A minimal, private empty metadata directory lets Git evaluate its own ignore rules.
            // No source metadata, index, configuration, or source file is created or edited.
            Directory.CreateDirectory(Path.Combine(scratch, "objects"));
            Directory.CreateDirectory(Path.Combine(scratch, "refs"));
            File.WriteAllText(Path.Combine(scratch, "HEAD"), "ref: refs/heads/launcher-inventory\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(scratch, "config"), "[core]\nrepositoryformatversion = 0\nbare = false\nignorecase = true\n", new UTF8Encoding(false));
            var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var batch in candidates.Chunk(500))
            {
                token.ThrowIfCancellationRequested();
                var input = string.Concat(batch.Select(item => item.Relative + (item.Directory ? "/" : "") + "\0"));
                var result = await GitAsync(root,
                    ["--git-dir=" + scratch, "--work-tree=" + root, "-c", "core.fsmonitor=false", "-c", "core.hooksPath=" + Path.Combine(scratch, "HEAD"),
                        "check-ignore", "--no-index", "-z", "--stdin"], token, standardInput: input).ConfigureAwait(false);
                if (result.ExitCode is not (0 or 1)) ThrowCommandFailure(result);
                foreach (var path in result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries)) ignored.Add(path.TrimEnd('/'));
            }
            return ignored;
        }
        catch (GitCommandExitUnconfirmedException) { uncertain = true; throw; }
        finally
        {
            if (!uncertain)
            {
                // Delete only the exact files/directories created here, never recursively.
                try
                {
                    File.Delete(Path.Combine(scratch, "HEAD"));
                    File.Delete(Path.Combine(scratch, "config"));
                    Directory.Delete(Path.Combine(scratch, "objects"));
                    Directory.Delete(Path.Combine(scratch, "refs"));
                    scratchHandle.Dispose();
                    Directory.Delete(scratch);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static void RequireSameConnectInventory(GitProjectConnectReview review, ConnectInventory current)
    {
        if (!review.Files.SequenceEqual(current.Files) || !review.ExcludedEntries.SequenceEqual(current.Excluded)
            || !review.Directories.SequenceEqual(current.Directories))
            throw new InvalidOperationException("The original project changed after review. Review the connection again; no source files were overwritten.");
    }

    private static void WriteConnectExclusions(string repository)
    {
        var info = Path.Combine(repository, ".git", "info");
        Directory.CreateDirectory(info);
        using var directory = OpenCloneDirectory(info, out _);
        using var file = new FileStream(Path.Combine(info, "exclude"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(file, new UTF8Encoding(false));
        writer.WriteLine("# Local exclusions added by Full Stack Launcher during project connection.");
        foreach (var name in ConnectExcludedDirectories) writer.WriteLine(name + "/");
        foreach (var pattern in ConnectExcludedFiles) writer.WriteLine(pattern);
    }

    private static async Task CopyConnectSourceAsync(GitProjectConnectReview review, CancellationToken token)
    {
        var root = review.CloneFolder;
        var handles = new List<SafeFileHandle>();
        try
        {
            // Hold every destination directory without delete sharing until the complete copy finishes.
            handles.Add(OpenCloneDirectory(root, out _));
            var current = root;
            var segments = review.Request.RepositorySubfolder!.Split('/');
            for (var index = 0; index < segments.Length; index++)
            {
                current = RequireWorkspaceChildPath(current, segments[index], "project");
                if (index == segments.Length - 1) CreateNewWorkspaceDirectory(current);
                else if (!Directory.Exists(current)) CreateNewWorkspaceDirectory(current);
                handles.Add(OpenCloneDirectory(current, out _));
            }
            var destinationDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { review.ProjectFolder };
            foreach (var sourceDirectory in review.Directories.Where(item => item.RelativePath != ".")
                .OrderBy(item => item.RelativePath.Count(character => character == '/')).ThenBy(item => item.RelativePath, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                var directory = Path.Combine(review.ProjectFolder, sourceDirectory.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                CreateNewWorkspaceDirectory(directory);
                handles.Add(OpenCloneDirectory(directory, out _));
                destinationDirectories.Add(directory);
            }
            foreach (var file in review.Files)
            {
                token.ThrowIfCancellationRequested();
                var source = Path.Combine(review.SourceFolder, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                var destination = Path.Combine(review.ProjectFolder, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                using var sourceDirectory = OpenCloneDirectory(Path.GetDirectoryName(source)!, out var identity);
                var relativeParent = Path.GetRelativePath(review.SourceFolder, Path.GetDirectoryName(source)!).Replace('\\', '/');
                if (!review.Directories.Any(directory => directory.RelativePath == relativeParent && directory.Identity == identity)
                    || !destinationDirectories.Contains(Path.GetDirectoryName(destination)!))
                    throw new InvalidOperationException("A source or destination directory changed during copying.");
                var sourceInfo = new FileInfo(source);
                if (sourceInfo.LinkTarget is not null) throw new InvalidOperationException("A source file became a link during copying.");
                await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                RequireConnectFileHandle(input, source);
                if (input.Length != file.Length || sourceInfo.LastWriteTimeUtc.Ticks != file.LastWriteTicks
                    || Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false)) != file.Hash)
                    throw new InvalidOperationException("A reviewed source file changed. The partial copy was retained for inspection.");
                input.Position = 0;
                await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await input.CopyToAsync(output, token).ConfigureAwait(false);
                    await output.FlushAsync(token).ConfigureAwait(false);
                    File.SetLastWriteTimeUtc(output.SafeFileHandle, new DateTime(file.LastWriteTicks, DateTimeKind.Utc));
                }
            }
        }
        finally { foreach (var handle in handles.AsEnumerable().Reverse()) handle.Dispose(); }
    }

    private static async Task RequireConnectFilesVisibleAsync(GitProjectConnectReview review, CancellationToken token)
    {
        foreach (var batch in review.Files.Chunk(500))
        {
            var input = string.Concat(batch.Select(file => review.Request.RepositorySubfolder + "/" + file.RelativePath + "\0"));
            var result = await GitAsync(review.CloneFolder, ["check-ignore", "--no-index", "-z", "--stdin"], token,
                standardInput: input).ConfigureAwait(false);
            if (result.ExitCode is not (0 or 1)) ThrowCommandFailure(result);
            if (result.Output.Length != 0)
            {
                var ignored = result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
                throw new InvalidOperationException("The destination repository's ignore rules hide copied project files. "
                    + "The clone was retained and the original project is unchanged. Review its ignore rules or choose another subfolder before connecting.\n"
                    + string.Join("\n", ignored.Take(20).Select(Sanitize))
                    + (ignored.Length > 20 ? "\nAdditional ignored files were omitted from this message." : ""));
            }
        }
    }

    private static void RequireConnectFileHandle(FileStream stream, string expectedPath)
    {
        // Pin the opened file first, then verify where Windows resolved it. A path-only LinkTarget check
        // before FileStream opens would permit a concurrent swap to an outside-source symlink.
        var buffer = new StringBuilder(32_768);
        var length = ReadConnectFinalPath(stream.SafeFileHandle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity
            || !ReadCloneDirectoryInformation(stream.SafeFileHandle, out var information)
            || (information.Attributes & 0x10) != 0)
            throw new InvalidOperationException("A source file's physical location could not be verified. Review the project before copying.");
        var actual = buffer.ToString();
        if (actual.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) actual = "\\\\" + actual[8..];
        else if (actual.StartsWith("\\\\?\\", StringComparison.Ordinal)) actual = actual[4..];
        if (!Path.GetFullPath(actual).Equals(Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A source file resolves through a link or changed during review. Guided copying cannot use it.");
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode,
        ExactSpelling = true, SetLastError = true)]
    private static extern uint ReadConnectFinalPath(SafeFileHandle file, StringBuilder path, uint length, uint flags);
}
