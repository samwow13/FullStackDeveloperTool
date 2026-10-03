using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FullStackLauncher.Models;

namespace FullStackLauncher.Services;

/// <summary>Local commit suggestions, isolated per checkout and never included in tracked source or launcher settings.</summary>
public static class AgentGitChangeStore
{
    private const int MaximumFileBytes = 16 * 1024 * 1024;
    private const int MaximumEntries = 10_000;
    private const string FileName = "launcher-agent-changes.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly Regex UpdateIdPattern = new(@"\A[A-Za-z0-9][A-Za-z0-9._:/-]{0,99}\z", RegexOptions.CultureInvariant);
    private static readonly Regex RemotePattern = new(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,100}\z", RegexOptions.CultureInvariant);

    public static string ConnectionId(GitRemoteInfo remote)
    {
        if (!remote.UrlCanCopy || !RemotePattern.IsMatch(remote.Name))
            throw new InvalidOperationException("Choose a connection with a supported, credential-free remote URL.");
        var fetch = GitRepositoryService.ValidateRemoteUrl(remote.FetchUrl);
        var push = GitRepositoryService.ValidateRemoteUrl(string.IsNullOrWhiteSpace(remote.PushUrl) ? fetch : remote.PushUrl);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(remote.Name + "\n" + fetch + "\n" + push)));
    }

    public static Task<AgentGitConnectionSelection?> ReadSelectionAsync(string root, CancellationToken token = default) =>
        AccessAsync(root, false, document => (document.Selection, false), token);

    public static Task SelectConnectionAsync(string root, string remoteName, string connectionId, CancellationToken token = default)
    {
        ValidateConnection(remoteName, connectionId);
        return AccessAsync(root, true, document =>
        {
            var selection = new AgentGitConnectionSelection(remoteName, connectionId);
            if (document.Selection == selection) return (true, false);
            document.Selection = selection;
            return (true, true);
        }, token);
    }

    public static Task<AgentGitSummaryBatch> ReadPendingAsync(string root, string branch, string remoteName,
        string connectionId, CancellationToken token = default)
    {
        ValidateScope(branch, remoteName, connectionId);
        return AccessAsync(root, false, document => (new AgentGitSummaryBatch
        {
            RepositoryRoot = GitConnectionService.NormalizeRoot(root), Branch = branch,
            RemoteName = remoteName, ConnectionId = connectionId,
            Entries = document.Entries.Where(entry => Matches(entry, branch, remoteName, connectionId)
                && entry.Summary.ConsumedCommitId is null).Select(entry => entry.Summary).ToArray()
        }, false), token);
    }

    public static Task<AgentGitSummaryEntry> RecordAsync(string root, string branch, string remoteName, string connectionId,
        string updateId, IReadOnlyList<string> bullets, CancellationToken token = default)
    {
        ValidateScope(branch, remoteName, connectionId);
        if (updateId is null || !UpdateIdPattern.IsMatch(updateId))
            throw new InvalidOperationException("Use a stable updateId of 1–100 letters, digits, dots, colons, slashes, underscores or hyphens.");
        var normalized = NormalizeBullets(bullets);
        return AccessAsync(root, true, document =>
        {
            var existing = document.Entries.FirstOrDefault(entry => entry.Summary.UpdateId == updateId);
            if (existing is not null)
            {
                if (!Matches(existing, branch, remoteName, connectionId)
                    || !existing.Summary.Bullets.SequenceEqual(normalized, StringComparer.Ordinal))
                    throw new InvalidOperationException("This updateId already identifies a different change report. Retry the original report, or use a new updateId for a separate update.");
                return (existing.Summary, false);
            }
            if (document.Selection is { } selected && (selected.RemoteName != remoteName || selected.ConnectionId != connectionId))
                throw new InvalidOperationException("The active Git connection changed. Refresh the project's Git connections before reporting changes.");
            if (document.Entries.Count >= MaximumEntries)
                throw new InvalidOperationException("The local agent change ledger has reached its capacity. Existing reports were preserved; no new report was saved.");
            var summary = new AgentGitSummaryEntry
            {
                Id = Guid.NewGuid().ToString("N"), UpdateId = updateId, Bullets = normalized, CreatedAt = DateTimeOffset.UtcNow
            };
            document.Entries.Add(new StoredEntry
            {
                Branch = branch, RemoteName = remoteName, ConnectionId = connectionId, Summary = summary
            });
            return (summary, true);
        }, token, branch);
    }

    public static Task MarkViewedAsync(AgentGitSummaryBatch batch, CancellationToken token = default)
    {
        ValidateScope(batch.Branch, batch.RemoteName, batch.ConnectionId);
        return AccessAsync(batch.RepositoryRoot, true, document =>
        {
            var viewedAt = DateTimeOffset.UtcNow;
            var changed = false;
            foreach (var displayed in batch.Entries)
            {
                var stored = document.Entries.SingleOrDefault(entry => entry.Summary.Id == displayed.Id);
                if (stored is null || !Matches(stored, batch.Branch, batch.RemoteName, batch.ConnectionId)
                    || stored.Summary.UpdateId != displayed.UpdateId || stored.Summary.CreatedAt != displayed.CreatedAt
                    || !stored.Summary.Bullets.SequenceEqual(displayed.Bullets, StringComparer.Ordinal))
                    throw new InvalidOperationException("The displayed agent summaries changed. Refresh the preview before marking them viewed.");
                if (stored.Summary.ConsumedCommitId is not null || stored.Summary.ViewedAt is not null) continue;
                stored.Summary = stored.Summary with { ViewedAt = viewedAt };
                changed = true;
            }
            return (true, changed);
        }, token);
    }

    public static Task ConsumeAsync(AgentGitSummaryBatch batch, string confirmedCommitId, CancellationToken token = default)
    {
        ValidateScope(batch.Branch, batch.RemoteName, batch.ConnectionId);
        if (!IsCommit(confirmedCommitId)) throw new InvalidOperationException("A confirmed local commit is required before consuming agent summaries.");
        return AccessAsync(batch.RepositoryRoot, true, document =>
        {
            var changed = false;
            foreach (var reviewed in batch.Entries)
            {
                var stored = document.Entries.SingleOrDefault(entry => entry.Summary.Id == reviewed.Id);
                if (stored is null || !Matches(stored, batch.Branch, batch.RemoteName, batch.ConnectionId)
                    || stored.Summary.UpdateId != reviewed.UpdateId
                    || !stored.Summary.Bullets.SequenceEqual(reviewed.Bullets, StringComparer.Ordinal))
                    throw new InvalidOperationException("The reviewed agent summaries changed. The local commit remains saved; review the change ledger before continuing.");
                if (stored.Summary.ConsumedCommitId is not null)
                {
                    if (stored.Summary.ConsumedCommitId != confirmedCommitId)
                        throw new InvalidOperationException("A reviewed agent summary was already included in another local commit. Review local history before continuing.");
                    continue;
                }
                stored.Summary = stored.Summary with { ConsumedCommitId = confirmedCommitId };
                changed = true;
            }
            return (true, changed);
        }, token);
    }

    private static bool Matches(StoredEntry entry, string branch, string remoteName, string connectionId) =>
        entry.Branch == branch && entry.RemoteName == remoteName && entry.ConnectionId == connectionId;

    private static string[] NormalizeBullets(IReadOnlyList<string> bullets)
    {
        if (bullets is null || bullets.Count is < 1 or > 5)
            throw new InvalidOperationException("Supply 1–5 short change bullets.");
        var result = new List<string>();
        foreach (var value in bullets)
        {
            if (value is null || value.Length > 256 || value.Any(char.IsControl))
                throw new InvalidOperationException("Each change bullet must be one plain-text line, without control characters.");
            var bullet = value.Trim();
            if (bullet.StartsWith("- ", StringComparison.Ordinal) || bullet.StartsWith("* ", StringComparison.Ordinal)
                || bullet.StartsWith("• ", StringComparison.Ordinal)) bullet = bullet[2..].Trim();
            bullet = string.Join(" ", bullet.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (bullet.Length is < 1 or > 120)
                throw new InvalidOperationException("Keep each change bullet between 1 and 120 characters. Summarize the change without logs or secrets.");
            if (!result.Contains(bullet, StringComparer.OrdinalIgnoreCase)) result.Add(bullet);
        }
        return result.ToArray();
    }

    private static void ValidateConnection(string remoteName, string connectionId)
    {
        if (remoteName is null || !RemotePattern.IsMatch(remoteName)
            || connectionId is null || connectionId.Length != 64 || !connectionId.All(Uri.IsHexDigit))
            throw new InvalidOperationException("Refresh the project's Git connections and use the returned connection identity.");
    }

    private static void ValidateScope(string branch, string remoteName, string connectionId)
    {
        ValidateConnection(remoteName, connectionId);
        if (string.IsNullOrWhiteSpace(branch) || branch.Length > 1024 || branch.Any(char.IsControl))
            throw new InvalidOperationException("A named local branch is required for agent change summaries.");
    }

    private static bool IsCommit(string value) => value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);

    private static Task<T> AccessAsync<T>(string root, bool write, Func<StoreDocument, (T Result, bool Changed)> action,
        CancellationToken token, string? expectedBranch = null) =>
        Task.Run(async () =>
        {
            var path = await ResolvePathAsync(root, token).ConfigureAwait(false);
            // Passive reads need no lock file: atomic replacement supplies a complete old or new snapshot.
            await using var writerLock = write ? await LockAsync(path + ".lock", token).ConfigureAwait(false) : null;
            if (expectedBranch is not null)
            {
                var current = await GitBranchReader.ReadAsync(root, token).ConfigureAwait(false);
                if (current.State != GitBranchState.Branch || current.DisplayText != expectedBranch)
                    throw new InvalidOperationException("The current Git branch changed. Refresh the project's Git connections before reporting changes.");
            }
            var document = await ReadAsync(path, token).ConfigureAwait(false);
            var (result, changed) = action(document);
            if (changed)
            {
                if (!write) throw new InvalidOperationException("A read-only change-ledger operation attempted a write.");
                await SaveAsync(path, document, token).ConfigureAwait(false);
            }
            return result;
        }, token);

    private static async Task<string> ResolvePathAsync(string root, CancellationToken token)
    {
        var normalized = GitConnectionService.NormalizeRoot(root);
        var actual = await GitRepositoryService.GitAsync(normalized, ["rev-parse", "--show-toplevel"], token).ConfigureAwait(false);
        if (actual.ExitCode != 0 || !string.Equals(normalized,
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(actual.Output.TrimEnd('\r', '\n'))), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The agent change ledger requires the current, accessible repository root.");
        var metadata = await GitRepositoryService.GitAsync(normalized, ["rev-parse", "--absolute-git-dir"], token).ConfigureAwait(false);
        var directory = metadata.Output.TrimEnd('\r', '\n');
        if (metadata.ExitCode != 0 || !Path.IsPathFullyQualified(directory) || directory.Any(char.IsControl) || !Directory.Exists(directory))
            throw new InvalidOperationException("The checkout's Git metadata is unavailable. Agent summaries were not changed.");
        return Path.Combine(directory, FileName);
    }

    private static async Task<FileStream> LockAsync(string path, CancellationToken token)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { await Task.Delay(75, token).ConfigureAwait(false); }
        }
    }

    private static async Task<StoreDocument> ReadAsync(string path, CancellationToken token)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > MaximumFileBytes) throw new InvalidDataException();
            using var bytes = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                if (bytes.Length + read > MaximumFileBytes) throw new InvalidDataException();
                bytes.Write(buffer, 0, read);
            }
            var document = JsonSerializer.Deserialize<StoreDocument>(bytes.ToArray(), Json) ?? throw new InvalidDataException();
            ValidateDocument(document);
            return document;
        }
        catch (FileNotFoundException) { return new StoreDocument(); }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidOperationException("The local agent change ledger is unreadable or unsupported. It was preserved; repair it before saving, viewing or consuming summaries.");
        }
    }

    private static void ValidateDocument(StoreDocument document)
    {
        if (document.Version is not (1 or 2) || document.Entries is null || document.Entries.Count > MaximumEntries)
            throw new InvalidDataException();
        if (document.Selection is { } selection) ValidateConnection(selection.RemoteName, selection.ConnectionId);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var updates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in document.Entries)
        {
            if (entry is null || entry.Summary is not { } summary || !Guid.TryParseExact(summary.Id, "N", out _)
                || !ids.Add(summary.Id) || summary.UpdateId is null || !UpdateIdPattern.IsMatch(summary.UpdateId)
                || !updates.Add(summary.UpdateId) || summary.CreatedAt == default
                || summary.ViewedAt == default(DateTimeOffset)
                || (document.Version == 1 && summary.ViewedAt is not null)
                || (summary.ConsumedCommitId is { } commit && !IsCommit(commit))) throw new InvalidDataException();
            ValidateScope(entry.Branch, entry.RemoteName, entry.ConnectionId);
            if (!NormalizeBullets(summary.Bullets).SequenceEqual(summary.Bullets, StringComparer.Ordinal)) throw new InvalidDataException();
        }
    }

    private static async Task SaveAsync(string path, StoreDocument document, CancellationToken token)
    {
        // Upgrade only during a real save; older launchers reject v2 instead of dropping viewed metadata.
        document.Version = 2;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Json);
        if (bytes.Length > MaximumFileBytes) throw new InvalidOperationException("The local agent change ledger is full. Existing reports were preserved.");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private sealed class StoreDocument
    {
        [JsonRequired] public int Version { get; set; } = 2;
        public AgentGitConnectionSelection? Selection { get; set; }
        [JsonRequired] public List<StoredEntry> Entries { get; set; } = [];
    }

    private sealed class StoredEntry
    {
        [JsonRequired] public string Branch { get; set; } = "";
        [JsonRequired] public string RemoteName { get; set; } = "";
        [JsonRequired] public string ConnectionId { get; set; } = "";
        [JsonRequired] public AgentGitSummaryEntry Summary { get; set; } = new();
    }
}
