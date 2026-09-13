using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FullStackLauncher.Models;
using Npgsql;

namespace FullStackLauncher.Services;

/// <summary>Prepares credential-free EF SQL and applies only the reviewed forward plan to an existing database.</summary>
public static class DatabaseMigrationService
{
    private const string Configuration = "LauncherMigrations";
    private const string Context = "ApplicationDbContext";
    private const string FactoryFile = "ApplicationDbContextDesignTimeFactory.cs";
    private const int MaximumSqlCharacters = 8 * 1024 * 1024;
    private const long AdvisoryLockKey = 4846235720972373324;
    private static readonly SemaphoreSlim PreparationGate = new(1, 1);

    public static IReadOnlyList<DatabaseMigrationProject> DiscoverProjects(IEnumerable<string> folders)
    {
        var projects = new Dictionary<string, DatabaseMigrationProject>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            try
            {
                var root = Path.GetFullPath(folder);
                if (!Directory.Exists(root)) continue;
                // Configured service roots and their immediate children only; never scan a drive or evaluate MSBuild.
                foreach (var directory in new[] { root }.Concat(Directory.EnumerateDirectories(root)))
                foreach (var file in Directory.EnumerateFiles(directory, "*.csproj"))
                {
                    if (HasOfflineFactory(file))
                        projects[file] = new(Path.GetFileNameWithoutExtension(file), file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return projects.Values.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static async Task<DatabaseMigrationPlan> PrepareAsync(string projectPath, DatabaseConnectionSource source,
        string database, IProgress<string>? progress, CancellationToken token)
    {
        await PreparationGate.WaitAsync(token);
        try
        {
            projectPath = ValidateProject(projectPath);
            ValidateDatabase(database);
            var folder = Path.GetDirectoryName(projectPath)!;
            var fingerprint = await FingerprintProjectAsync(projectPath, token);
            progress?.Report("Checking the repository EF tool…");
            var efTool = await ResolveEfToolAsync(folder, progress, token);
            progress?.Report("Building migration metadata in a separate output configuration…");
            await RunDotnetAsync(folder, ["build", projectPath, "--configuration", Configuration,
                "--nologo", "--verbosity", "quiet", "-p:UseAppHost=false"], "Migration project build", false, token);
            progress?.Report("Reading repository migrations without connecting the API to a database…");
            var repository = ReadMigrationIds(await RunDotnetAsync(folder,
                EfArguments(efTool, projectPath, ["migrations", "list", "--no-connect", "--json"]),
                "Repository migration discovery", true, token));

            progress?.Report("Reading the selected target identity, schema and migration history…");
            await using var connection = source.CreateConnection(database);
            await connection.OpenAsync(token);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
            await ExecuteAsync(connection, transaction, "SET TRANSACTION READ ONLY; SET LOCAL search_path = public, pg_catalog; SET LOCAL standard_conforming_strings = on; SET LOCAL row_security = off", token);
            var identity = await ReadIdentityAsync(connection, transaction, database, token);
            var applied = await ReadHistoryAsync(connection, transaction, token);
            var schemaFingerprint = await ReadSchemaFingerprintAsync(connection, transaction, token);
            var blocked = CheckHistory(repository, applied);
            if (blocked is null && applied.Count == 0 && await HasPublicTablesAsync(connection, transaction, token))
                blocked = "The target contains public tables but has no applied EF migration history. Resolve its baseline before applying an initial migration.";
            await transaction.CommitAsync(token);

            var pending = blocked is null ? repository.Skip(applied.Count).ToArray() : [];
            var sql = "";
            if (blocked is null && pending.Length > 0)
            {
                progress?.Report("Generating the exact forward SQL for review…");
                var output = Path.Combine(Path.GetTempPath(), "launcher-migrations-" + Guid.NewGuid().ToString("N") + ".sql");
                try
                {
                    await RunDotnetAsync(folder, EfArguments(efTool, projectPath, ["migrations", "script",
                        applied.LastOrDefault() ?? "0", repository[^1], "--no-transactions", "--output", output]),
                        "Migration SQL generation", false, token);
                    var file = new FileInfo(output);
                    if (!file.Exists || file.Length > MaximumSqlCharacters * 4L)
                        throw new DatabaseMigrationException("The migration SQL is missing or too large for an interactive review.");
                    sql = await File.ReadAllTextAsync(output, token);
                    if (string.IsNullOrWhiteSpace(sql) || sql.Length > MaximumSqlCharacters)
                        throw new DatabaseMigrationException("The migration SQL is empty or too large for an interactive review.");
                    blocked = ValidateTransactionalSql(sql);
                }
                finally
                {
                    try { File.Delete(output); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
            if (!string.Equals(fingerprint, await FingerprintProjectAsync(projectPath, token), StringComparison.Ordinal))
                throw new DatabaseMigrationException("The migration project changed while preparing SQL. Prepare a new plan.");

            return new(projectPath, source, database, repository, applied, pending, sql, Hash(sql),
                identity, fingerprint, schemaFingerprint, blocked);
        }
        catch (OperationCanceledException) { throw; }
        catch (DatabaseMigrationException) { throw; }
        catch (Exception ex) { throw new DatabaseMigrationException(DescribeError(ex)); }
        finally { PreparationGate.Release(); }
    }

    public static async Task<DatabaseMigrationApplyResult> ApplyAsync(DatabaseMigrationPlan plan,
        DatabaseConnectionSource source, string database, string typedTarget, bool backupConfirmed,
        IProgress<string>? progress, CancellationToken token)
    {
        var commitStarted = false;
        try
        {
            if (!plan.CanApply) throw new DatabaseMigrationException("Prepare and review a valid pending migration plan first.");
            if (!backupConfirmed || !string.Equals(typedTarget, plan.ConfirmationText, StringComparison.Ordinal))
                throw new DatabaseMigrationException("Confirm the exact server/database and acknowledge a verified backup before applying.");
            if (source.Id != plan.SourceId || source.Server != plan.SourceServer || database != plan.TargetDatabase)
                throw new DatabaseMigrationException("The target selection changed. Prepare and review a new plan.");
            if (DateTimeOffset.UtcNow - plan.CreatedAtUtc > TimeSpan.FromMinutes(30))
                throw new DatabaseMigrationException("This review is more than 30 minutes old. Prepare a fresh plan before applying.");
            if (Hash(plan.Sql) != plan.SqlSha256 || ValidateTransactionalSql(plan.Sql) is not null)
                throw new DatabaseMigrationException("The reviewed SQL is no longer valid. Prepare a new plan.");
            if (await FingerprintProjectAsync(ValidateProject(plan.ProjectPath), token) != plan.ProjectFingerprint)
                throw new DatabaseMigrationException("The migration project changed since review. Prepare and review a new plan.");

            progress?.Report("Connecting to the confirmed existing target and acquiring the migration lock…");
            await using var connection = source.CreateConnection(database);
            await connection.OpenAsync(token);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            await ExecuteAsync(connection, transaction, "SET LOCAL search_path = public, pg_catalog; SET LOCAL standard_conforming_strings = on; SET LOCAL row_security = off; SET LOCAL lock_timeout = '10s'; SET LOCAL statement_timeout = '10min'", token);
            await using (var command = new NpgsqlCommand("SELECT pg_catalog.pg_try_advisory_xact_lock($1)", connection, transaction))
            {
                command.Parameters.AddWithValue(AdvisoryLockKey);
                if (await command.ExecuteScalarAsync(token) is not true)
                    throw new DatabaseMigrationException("Another launcher is applying migrations to this database. Wait for it to finish, then prepare a fresh plan.");
            }
            if (await ReadIdentityAsync(connection, transaction, database, token) != plan.DatabaseIdentity)
                throw new DatabaseMigrationException("The connected target identity changed since review. Prepare a new plan.");
            if (await HistoryExistsAsync(connection, transaction, token))
                await ExecuteAsync(connection, transaction, "LOCK TABLE public.\"__EFMigrationsHistory\" IN ACCESS EXCLUSIVE MODE", token);
            var applied = await ReadHistoryAsync(connection, transaction, token);
            if (!applied.SequenceEqual(plan.AppliedMigrations, StringComparer.Ordinal))
                throw new DatabaseMigrationException("Target migration history changed since review. Nothing was applied; prepare a fresh plan.");
            if (await ReadSchemaFingerprintAsync(connection, transaction, token) != plan.SchemaFingerprint)
                throw new DatabaseMigrationException("The target schema changed since review. Nothing was applied; compare again and prepare a fresh plan.");

            progress?.Report($"Applying {plan.PendingMigrations.Count} reviewed migration(s) in one transaction…");
            await ExecuteAsync(connection, transaction, plan.Sql, token, 600);
            var completed = await ReadHistoryAsync(connection, transaction, token);
            if (!completed.SequenceEqual(plan.RepositoryMigrations, StringComparer.Ordinal))
                throw new DatabaseMigrationException("Post-migration history did not match the reviewed plan. The transaction was not committed; refresh the target before retrying.");
            token.ThrowIfCancellationRequested();
            progress?.Report("Committing the migration transaction; waiting for database confirmation…");
            commitStarted = true;
            // Once COMMIT is sent, cancellation cannot establish whether the database committed.
            // Wait for the server acknowledgement and report transport uncertainty without auto-retry.
            await transaction.CommitAsync(CancellationToken.None);
            return new(plan.PendingMigrations.Count, DateTimeOffset.UtcNow,
                $"Applied {plan.PendingMigrations.Count} migration(s) to {plan.TargetLabel}. The database confirmed the commit. Refresh the comparison to inspect the resulting schema.");
        }
        catch (Exception) when (commitStarted)
        {
            throw new DatabaseMigrationException("The connection ended while confirming COMMIT. The outcome is unknown. Do not retry this plan; reconnect, read migration history and compare the schema before deciding what to do next.");
        }
        catch (OperationCanceledException) { throw; }
        catch (DatabaseMigrationException) { throw; }
        catch (Exception ex) { throw new DatabaseMigrationException(DescribeError(ex) + " The transaction was not committed. Refresh the target before preparing another plan."); }
    }

    public static string DescribeError(Exception exception) => exception switch
    {
        DatabaseMigrationException => exception.Message,
        OperationCanceledException => "Operation canceled. Refresh the target and prepare a fresh plan before applying.",
        PostgresException { SqlState: "42501" } => "The database role lacks the required catalog, migration-history, or schema permissions.",
        PostgresException { SqlState: "55P03" } => "The database is busy and the schema lock could not be acquired within 10 seconds. Stop competing deployment activity and try again.",
        PostgresException { SqlState: "25006" } => "The selected target is read-only. Choose a writable primary database for migration application.",
        PostgresException { SqlState: "57014" } => "The database operation was canceled or exceeded its time limit.",
        PostgresException ex => $"PostgreSQL rejected the operation (SQLSTATE {ex.SqlState}). Review the target permissions and SQL.",
        NpgsqlException or TimeoutException => "Could not complete the PostgreSQL connection. Check the configured server, credentials, firewall and TLS settings.",
        Win32Exception => "The .NET SDK could not be started. Install or repair the SDK required by the selected API project.",
        IOException or UnauthorizedAccessException => "The migration project or generated SQL could not be read. Check the project folder and file access.",
        _ => "The migration operation could not be completed. Refresh the target and check the project configuration."
    };

    private static bool HasOfflineFactory(string projectPath)
    {
        var folder = Path.GetDirectoryName(projectPath)!;
        var factory = Path.Combine(folder, "Data", FactoryFile);
        return Directory.Exists(Path.Combine(folder, "Migrations")) && File.Exists(factory)
            && File.ReadAllText(factory).Contains("--launcher-script-only", StringComparison.Ordinal);
    }

    private static string ValidateProject(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) throw new DatabaseMigrationException("Choose the API migration project.");
        var path = Path.GetFullPath(projectPath);
        if (!File.Exists(path) || !path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || !HasOfflineFactory(path))
            throw new DatabaseMigrationException("This project does not provide the launcher's offline ApplicationDbContext migration factory. Select the configured CRM API project.");
        return path;
    }

    private static void ValidateDatabase(string database)
    {
        if (string.IsNullOrWhiteSpace(database)) throw new DatabaseMigrationException("Choose an existing target database explicitly.");
    }

    private static List<string> EfArguments(string tool, string project, IEnumerable<string> command) =>
        [tool, .. command, "--project", project, "--startup-project", project, "--context", Context,
            "--configuration", Configuration, "--no-build", "--no-color", "--", "--launcher-script-only"];

    private static async Task<string> ResolveEfToolAsync(string folder, IProgress<string>? progress, CancellationToken token)
    {
        string? version = null;
        for (var directory = new DirectoryInfo(folder); directory is not null; directory = directory.Parent)
        {
            var manifest = Path.Combine(directory.FullName, ".config", "dotnet-tools.json");
            if (!File.Exists(manifest)) continue;
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(manifest, token));
            if (document.RootElement.TryGetProperty("tools", out var tools)
                && tools.TryGetProperty("dotnet-ef", out var ef)
                && ef.TryGetProperty("version", out var pinned))
            { version = pinned.GetString(); break; }
            if (document.RootElement.TryGetProperty("isRoot", out var isRoot) && isRoot.ValueKind == JsonValueKind.True) break;
        }
        if (version is null || !Regex.IsMatch(version, @"^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$"))
            throw new DatabaseMigrationException("The migration project needs a repository dotnet-tools.json manifest with a pinned dotnet-ef version.");

        async Task<bool> IsMatchingToolAsync(string tool)
        {
            try
            {
                var output = await RunDotnetAsync(folder, [tool, "--version"], "EF tool discovery", true, token);
                return output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains(version, StringComparer.Ordinal);
            }
            catch (DatabaseMigrationException) { return false; }
        }

        if (await IsMatchingToolAsync("ef")) return "ef";
        // A restored NuGet package can already be present even when the local command shim was not restored.
        // Use only the manifest's exact version, and verify it before allowing offline metadata generation.
        var packageRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrWhiteSpace(packageRoot))
            packageRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        var cachedTools = Path.Combine(packageRoot, "dotnet-ef", version.ToLowerInvariant(), "tools");
        if (Directory.Exists(cachedTools))
        {
            foreach (var runtime in Directory.EnumerateDirectories(cachedTools).Order(StringComparer.Ordinal))
            {
                var candidate = Path.Combine(runtime, "any", "dotnet-ef.dll");
                if (File.Exists(candidate) && await IsMatchingToolAsync(candidate)) return candidate;
            }
        }
        progress?.Report("Restoring the repository's pinned EF tool…");
        await RunDotnetAsync(folder, ["tool", "restore"], "EF tool restore", false, token);
        if (!await IsMatchingToolAsync("ef"))
            throw new DatabaseMigrationException("The restored EF tool does not match the repository's pinned version. Repair the local tool restore before preparing migrations.");
        return "ef";
    }

    private static IReadOnlyList<string> ReadMigrationIds(string output)
    {
        // EF may emit notices before the JSON payload. Parse only a complete JSON array.
        var start = output.IndexOf('[');
        var end = output.LastIndexOf(']');
        if (start < 0 || end < start) throw new DatabaseMigrationException("EF did not return a readable repository migration list.");
        using var document = JsonDocument.Parse(output[start..(end + 1)]);
        var values = document.RootElement.EnumerateArray().Select(value => value.GetProperty("id").GetString() ?? "").ToArray();
        if (values.Any(value => !Regex.IsMatch(value, @"^\d{14}_[A-Za-z0-9_]+$")) || values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new DatabaseMigrationException("The repository contains invalid or duplicate migration identifiers.");
        if (!values.SequenceEqual(values.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new DatabaseMigrationException("The repository migrations are not in a supported forward order.");
        return values;
    }

    private static string? CheckHistory(IReadOnlyList<string> repository, IReadOnlyList<string> applied)
    {
        if (repository.Count == 0) return "The selected project contains no migrations.";
        var unknown = applied.Except(repository, StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0) return "The target has migrations absent from this checkout. Use the matching/newer code before applying; downgrades are not supported.";
        if (!applied.SequenceEqual(repository.Take(applied.Count), StringComparer.Ordinal))
            return "The target migration history has gaps or a different ordering. Resolve the baseline before applying; this tool will not invent or delete history.";
        return null;
    }

    private static async Task<string> ReadIdentityAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string database, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT current_database(), d.oid::text, COALESCE(inet_server_addr()::text, 'local'),
                COALESCE(inet_server_port()::text, 'local'), session_user, current_user,
                current_setting('server_version_num'), pg_is_in_recovery()
            FROM pg_catalog.pg_database d WHERE d.datname = current_database()
            """, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token) || reader.GetString(0) != database)
            throw new DatabaseMigrationException("The server did not open the exact selected database.");
        if (reader.GetBoolean(7)) throw new DatabaseMigrationException("This target is a recovery/replica server. Select the primary database for migration preparation.");
        return Hash(string.Join("\n", Enumerable.Range(0, 7).Select(reader.GetString)));
    }

    private static async Task<bool> HistoryExistsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        var found = false;
        await using (var command = new NpgsqlCommand("""
            SELECT n.nspname, c.relkind::text, c.relrowsecurity, c.relforcerowsecurity
            FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relname = '__EFMigrationsHistory' AND n.nspname <> 'information_schema' AND n.nspname !~ '^pg_'
            ORDER BY n.nspname LIMIT 2
            """, connection, transaction))
        {
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                if (found || reader.GetString(0) != "public")
                    throw new DatabaseMigrationException("Migration history is ambiguous or uses a custom schema. This migration tool supports only the CRM's single public.__EFMigrationsHistory table.");
                if (reader.GetString(1) != "r" || reader.GetBoolean(2) || reader.GetBoolean(3))
                    throw new DatabaseMigrationException("The target migration history must be a regular public table without row-level security. Views, foreign tables and filtered history are not supported.");
                found = true;
            }
        }
        if (!found) return false;
        await using (var command = new NpgsqlCommand("""
            SELECT a.attname, t.typname, a.attnotnull
            FROM pg_catalog.pg_attribute a
            JOIN pg_catalog.pg_type t ON t.oid = a.atttypid
            WHERE a.attrelid = pg_catalog.to_regclass('public."__EFMigrationsHistory"')
                AND NOT a.attisdropped AND a.attnum > 0 AND a.attname IN ('MigrationId', 'ProductVersion')
            """, connection, transaction))
        {
            var columns = new HashSet<string>(StringComparer.Ordinal);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                if (reader.GetString(1) is not ("varchar" or "text") || !reader.GetBoolean(2))
                    throw new DatabaseMigrationException("The target migration-history columns are not the expected required string columns. Resolve its baseline before applying.");
                columns.Add(reader.GetString(0));
            }
            if (columns.Count != 2)
                throw new DatabaseMigrationException("The target migration-history table is missing required columns. Resolve its baseline before applying.");
        }
        return true;
    }

    private static async Task<IReadOnlyList<string>> ReadHistoryAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        if (!await HistoryExistsAsync(connection, transaction, token)) return [];
        var values = new List<string>();
        await using var command = new NpgsqlCommand("SELECT pg_catalog.left(\"MigrationId\", 151), pg_catalog.left(\"ProductVersion\", 33) FROM public.\"__EFMigrationsHistory\" ORDER BY \"MigrationId\" COLLATE \"C\" LIMIT 20001", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var id = reader.GetString(0);
            var version = reader.GetString(1);
            if (values.Count >= 20000 || id.Length > 150 || !Regex.IsMatch(id, @"^\d{14}_[A-Za-z0-9_]+$")
                || string.IsNullOrWhiteSpace(version) || version.Length > 32
                || (values.Count > 0 && values[^1] == id))
                throw new DatabaseMigrationException("The target migration history contains invalid, duplicate or excessive records. Resolve its baseline before applying.");
            values.Add(id);
        }
        return values;
    }

    private static async Task<bool> HasPublicTablesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'public' AND c.relkind IN ('r','p','v','m','f') AND c.relname <> '__EFMigrationsHistory')
            """, connection, transaction);
        return await command.ExecuteScalarAsync(token) is true;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, CancellationToken token, int timeout = 30)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = timeout };
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<string> FingerprintProjectAsync(string projectPath, CancellationToken token)
    {
        var root = Path.GetDirectoryName(projectPath)!;
        var files = new List<string>();
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(root);
        while (pendingDirectories.TryPop(out var current))
        {
            token.ThrowIfCancellationRequested();
            files.AddRange(Directory.EnumerateFiles(current).Where(file =>
                Path.GetExtension(file).ToLowerInvariant() is ".cs" or ".csproj" or ".props" or ".targets"));
            foreach (var child in Directory.EnumerateDirectories(current))
            {
                var name = Path.GetFileName(child);
                if (name.Equals("bin", StringComparison.OrdinalIgnoreCase) || name.Equals("obj", StringComparison.OrdinalIgnoreCase)
                    || name.Equals(".git", StringComparison.OrdinalIgnoreCase)
                    || (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                pendingDirectories.Push(child);
            }
        }
        for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
        {
            foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json", ".config/dotnet-tools.json" })
            {
                var file = Path.Combine(directory.FullName, name);
                if (File.Exists(file) && !files.Contains(file, StringComparer.OrdinalIgnoreCase)) files.Add(file);
            }
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files.Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(Encoding.UTF8.GetBytes(file + "\0"));
            hash.AppendData(await File.ReadAllBytesAsync(file, token));
            hash.AppendData([0]);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static async Task<string> RunDotnetAsync(string workingDirectory, IEnumerable<string> arguments,
        string operation, bool captureOutput, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = workingDirectory, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var value in arguments) start.ArgumentList.Add(value);
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("ConnectionStrings__", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["DOTNET_NOLOGO"] = "1";
        using var process = new Process { StartInfo = start };
        process.Start();
        // Never forward compiler/EF output: custom project output and errors may contain configuration values.
        var stdout = DrainAsync(process.StandardOutput, captureOutput, timeout.Token);
        var stderr = DrainAsync(process.StandardError, false, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout;
            await stderr;
            if (process.ExitCode != 0)
                throw new DatabaseMigrationException($"{operation} failed. Check that the API builds and the pinned EF tool can restore. Detailed process output is withheld because it may contain configuration values.");
            return output;
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            try { await process.WaitForExitAsync(CancellationToken.None); }
            catch (InvalidOperationException) { }
            try { await Task.WhenAll(stdout, stderr); }
            catch (OperationCanceledException) { }
            if (token.IsCancellationRequested) throw;
            throw new DatabaseMigrationException($"{operation} exceeded five minutes. Check the SDK/package restore and try again.");
        }
    }

    private static async Task<string> DrainAsync(StreamReader reader, bool capture, CancellationToken token)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
            if (capture && output.Length < MaximumSqlCharacters) output.Append(buffer, 0, Math.Min(count, MaximumSqlCharacters - output.Length));
        return output.ToString();
    }

    private static string? ValidateTransactionalSql(string sql)
    {
        // Strip PostgreSQL comments, quoted identifiers and literal/dollar-quoted bodies before inspecting
        // top-level statements. A migration cannot escape the transaction around the reviewed plan.
        var statements = new List<List<string>>();
        var words = new List<string>();
        for (var index = 0; index < sql.Length;)
        {
            var ch = sql[index];
            if (char.IsWhiteSpace(ch)) { index++; continue; }
            if (ch == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
            { index = sql.IndexOf('\n', index + 2) is var end && end >= 0 ? end + 1 : sql.Length; continue; }
            if (ch == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                var depth = 1; index += 2;
                while (index < sql.Length && depth > 0)
                {
                    if (index + 1 < sql.Length && sql[index] == '/' && sql[index + 1] == '*') { depth++; index += 2; }
                    else if (index + 1 < sql.Length && sql[index] == '*' && sql[index + 1] == '/') { depth--; index += 2; }
                    else index++;
                }
                if (depth != 0) return "The generated SQL contains an unterminated comment and cannot be applied.";
                continue;
            }
            if (ch is '\'' or '"')
            {
                var quote = ch;
                var escapes = quote == '\'' && index > 0 && (sql[index - 1] is 'e' or 'E')
                    && (index < 2 || !char.IsLetterOrDigit(sql[index - 2]));
                var closed = false; index++;
                while (index < sql.Length)
                {
                    if (escapes && sql[index] == '\\') { index += 2; continue; }
                    if (sql[index++] != quote) continue;
                    if (index < sql.Length && sql[index] == quote) { index++; continue; }
                    closed = true; break;
                }
                if (!closed) return "The generated SQL contains an unterminated literal and cannot be applied.";
                continue;
            }
            if (ch == '$')
            {
                var match = Regex.Match(sql[index..], @"^\$(?:[A-Za-z_][A-Za-z0-9_]*)?\$");
                if (match.Success)
                {
                    var end = sql.IndexOf(match.Value, index + match.Length, StringComparison.Ordinal);
                    if (end < 0) return "The generated SQL contains an unterminated function body and cannot be applied.";
                    index = end + match.Length; continue;
                }
            }
            if (ch == ';') { if (words.Count > 0) statements.Add(words); words = []; index++; continue; }
            if (char.IsLetter(ch) || ch == '_')
            {
                var start = index++;
                while (index < sql.Length && (char.IsLetterOrDigit(sql[index]) || sql[index] is '_' or '$')) index++;
                words.Add(sql[start..index].ToUpperInvariant());
                continue;
            }
            if (ch == '\\') return "The generated SQL contains an unsupported client command. Apply it through a separately reviewed deployment process.";
            index++;
        }
        if (words.Count > 0) statements.Add(words);
        foreach (var statement in statements)
        {
            var first = statement[0];
            if (first is "BEGIN" or "START" or "COMMIT" or "END" or "ROLLBACK" or "ABORT" or "SAVEPOINT" or "RELEASE" or "PREPARE"
                or "VACUUM" or "CALL" or "COPY" or "DISCARD" or "SET" or "RESET"
                || statement.Contains("CONCURRENTLY", StringComparer.Ordinal)
                || (first is "CREATE" or "ALTER" or "DROP" && statement.Count > 1 && statement[1] is "DATABASE" or "TABLESPACE" or "SYSTEM"))
                return "This SQL includes transaction control, session changes or an operation requiring separate execution. Export and review it through your deployment process; the launcher only applies one atomic transaction.";
        }
        return null;
    }

    // The comparison reader supplies the same canonical metadata capture for review and apply.
    private static Task<string> ReadSchemaFingerprintAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
        => PostgresSchemaComparer.ReadFingerprintAsync(connection, transaction, token);
}
