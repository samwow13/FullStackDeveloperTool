using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace FullStackLauncher.ProjectTasks;

/// <summary>Reads the selected native CLI's local schema without starting a server or loading credentials.</summary>
internal static class CodexProtocolCapabilities
{
    private const int MaximumSchemaBytes = 256 * 1024;
    private const string DirectoryPrefix = "FullStackLauncher-codex-schema-";
    private enum SchemaCapability { ProjectConfigRead, ServiceTierSelection, StandardTierSelection }
    private sealed record ExecutableIdentity(string Path, long Length, long LastWriteTicks);
    private static readonly ConcurrentDictionary<(ExecutableIdentity, SchemaCapability), Lazy<Task<bool?>>> Cache = new();

    internal static Task<bool> SupportsProjectConfigReadAsync(string nativeExecutablePath,
        CancellationToken token) => ReadCapabilityAsync(nativeExecutablePath, SchemaCapability.ProjectConfigRead, token);

    internal static Task<bool> SupportsServiceTierSelectionAsync(string nativeExecutablePath,
        CancellationToken token) => ReadCapabilityAsync(nativeExecutablePath, SchemaCapability.ServiceTierSelection, token);

    internal static Task<bool> SupportsStandardTierSelectionAsync(string nativeExecutablePath,
        CancellationToken token) => ReadCapabilityAsync(nativeExecutablePath, SchemaCapability.StandardTierSelection, token);

    private static async Task<bool> ReadCapabilityAsync(string nativeExecutablePath,
        SchemaCapability capability, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ExecutableIdentity identity;
        string executable;
        try
        {
            if (!Path.IsPathFullyQualified(nativeExecutablePath)) return false;
            var file = new FileInfo(nativeExecutablePath);
            if (!file.Exists) return false;
            executable = file.FullName;
            identity = new(executable.ToUpperInvariant(), file.Length, file.LastWriteTimeUtc.Ticks);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException) { return false; }

        // Share unfinished reads; caller cancellation does not cancel another
        // caller's probe. The owned generator always has its own 15-second limit.
        var key = (identity, capability);
        var probe = Cache.GetOrAdd(key, _ => new Lazy<Task<bool?>>(
            () => ProbeAsync(executable, identity, capability), LazyThreadSafetyMode.ExecutionAndPublication));
        var result = await probe.Value.WaitAsync(token).ConfigureAwait(false);
        if (result is null)
            Cache.TryRemove(new KeyValuePair<(ExecutableIdentity, SchemaCapability), Lazy<Task<bool?>>>(key, probe));
        // Missing capability and an unavailable schema both fail closed. Only
        // completed schema evidence is cached, so transient failures can recover.
        return result == true;
    }

    private static async Task<bool?> ProbeAsync(string executable, ExecutableIdentity identity, SchemaCapability capability)
    {
        string? outputDirectory = null;
        Process? process = null;
        Task? stdoutDrain = null;
        Task? stderrDrain = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            if (!MatchesExecutableIdentity(executable, identity)) return null;
            var temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            outputDirectory = Path.Combine(temporaryRoot, DirectoryPrefix + Guid.NewGuid().ToString("N"));
            if (Directory.Exists(outputDirectory)) return null;
            Directory.CreateDirectory(outputDirectory);
            var start = new ProcessStartInfo
            {
                FileName = executable, WorkingDirectory = outputDirectory, UseShellExecute = false,
                CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            start.ArgumentList.Add("app-server");
            start.ArgumentList.Add("generate-json-schema");
            start.ArgumentList.Add("--out");
            start.ArgumentList.Add(outputDirectory);
            process = new Process { StartInfo = start };
            if (!process.Start()) return null;
            process.StandardInput.Close();
            stdoutDrain = DrainAsync(process.StandardOutput, timeout.Token);
            stderrDrain = DrainAsync(process.StandardError, timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(stdoutDrain, stderrDrain).WaitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0 || !MatchesExecutableIdentity(executable, identity)) return null;

            if (capability == SchemaCapability.ServiceTierSelection)
            {
                foreach (var schemaName in new[] { "ThreadStartParams", "ThreadStartResponse", "TurnStartParams" })
                {
                    var supported = await ReadStringPropertyAsync(outputDirectory, schemaName, "serviceTier", timeout.Token)
                        .ConfigureAwait(false);
                    if (supported != true) return supported;
                }
                return MatchesExecutableIdentity(executable, identity) ? true : null;
            }

            if (capability == SchemaCapability.StandardTierSelection)
            {
                var responseSupported = await ReadStringPropertyAsync(outputDirectory, "ThreadStartResponse", "serviceTier",
                    timeout.Token).ConfigureAwait(false);
                if (responseSupported != true) return responseSupported;
                var turnSupported = await ReadStringPropertyAsync(outputDirectory, "TurnStartParams", "serviceTierForTurn",
                    timeout.Token, requireStandardDescription: true).ConfigureAwait(false);
                return MatchesExecutableIdentity(executable, identity) ? turnSupported : null;
            }

            // Both historical and current generators place v2 parameter schemas
            // here. Never search unrelated files or parse generated server output.
            var schemaPath = Path.Combine(outputDirectory, "v2", "ConfigReadParams.json");
            var schemaFile = new FileInfo(schemaPath);
            if (!schemaFile.Exists || schemaFile.Length is <= 0 or > MaximumSchemaBytes
                || (schemaFile.Attributes & FileAttributes.ReparsePoint) != 0
                || (new DirectoryInfo(schemaFile.DirectoryName!).Attributes & FileAttributes.ReparsePoint) != 0)
                return null;
            await using var stream = new FileStream(schemaPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var document = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions
            {
                MaxDepth = 32
            }, timeout.Token).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("title", out var title) || title.ValueKind != JsonValueKind.String
                || title.GetString() != "ConfigReadParams"
                || !root.TryGetProperty("type", out var rootType) || rootType.ValueKind != JsonValueKind.String
                || rootType.GetString() != "object"
                || !root.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
                return null;
            var supportsProjectCwd = false;
            if (properties.TryGetProperty("cwd", out var cwd))
            {
                if (cwd.ValueKind != JsonValueKind.Object || !cwd.TryGetProperty("type", out var type)) return null;
                if (type.ValueKind == JsonValueKind.String)
                {
                    if (type.GetString() != "string") return null;
                    supportsProjectCwd = true;
                }
                else
                {
                    if (type.ValueKind != JsonValueKind.Array || type.GetArrayLength() is < 1 or > 2) return null;
                    foreach (var item in type.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String || item.GetString() is not ("string" or "null")) return null;
                        if (item.GetString() == "string") supportsProjectCwd = true;
                    }
                    if (!supportsProjectCwd) return null;
                }
            }
            // A replacement during generation/parsing cannot establish the
            // capability of the executable fingerprint captured by this caller.
            return MatchesExecutableIdentity(executable, identity) ? supportsProjectCwd : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception
            or JsonException or InvalidOperationException or ArgumentException or NotSupportedException
            or OperationCanceledException or System.Security.SecurityException) { return null; }
        finally
        {
            if (process is not null)
            {
                try { if (!process.HasExited) process.Kill(); }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { }
                if (stdoutDrain is not null && stderrDrain is not null)
                {
                    try { await Task.WhenAll(stdoutDrain, stderrDrain).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or TimeoutException) { }
                }
                process.Dispose();
            }
            if (outputDirectory is not null) DeleteOwnedDirectory(outputDirectory);
        }
    }

    private static async Task<bool?> ReadStringPropertyAsync(string directory, string schemaName,
        string propertyName, CancellationToken token, bool requireStandardDescription = false)
    {
        var schemaPath = Path.Combine(directory, "v2", schemaName + ".json");
        var schemaFile = new FileInfo(schemaPath);
        if (!schemaFile.Exists || schemaFile.Length is <= 0 or > MaximumSchemaBytes
            || (schemaFile.Attributes & FileAttributes.ReparsePoint) != 0
            || (new DirectoryInfo(schemaFile.DirectoryName!).Attributes & FileAttributes.ReparsePoint) != 0)
            return null;
        await using var stream = new FileStream(schemaPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var document = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 32 }, token)
            .ConfigureAwait(false);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("title", out var title) || title.ValueKind != JsonValueKind.String
            || title.GetString() != schemaName
            || !root.TryGetProperty("type", out var rootType) || rootType.ValueKind != JsonValueKind.String
            || rootType.GetString() != "object"
            || !root.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            return null;
        if (!properties.TryGetProperty(propertyName, out var property)) return false;
        if (property.ValueKind != JsonValueKind.Object || !property.TryGetProperty("type", out var type)) return null;
        if (requireStandardDescription && (!property.TryGetProperty("description", out var description)
            || description.ValueKind != JsonValueKind.String
            || !description.GetString()!.Contains("\"default\"", StringComparison.Ordinal)
            || !description.GetString()!.Contains("standard", StringComparison.OrdinalIgnoreCase))) return false;
        if (type.ValueKind == JsonValueKind.String) return type.GetString() == "string" ? true : null;
        if (type.ValueKind != JsonValueKind.Array || type.GetArrayLength() is < 1 or > 2) return null;
        var supportsString = false;
        foreach (var item in type.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not ("string" or "null")) return null;
            if (item.GetString() == "string") supportsString = true;
        }
        return supportsString ? true : null;
    }

    private static bool MatchesExecutableIdentity(string executable, ExecutableIdentity identity)
    {
        var file = new FileInfo(executable);
        return file.Exists && file.Length == identity.Length && file.LastWriteTimeUtc.Ticks == identity.LastWriteTicks
            && string.Equals(file.FullName, identity.Path, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false) != 0) { }
    }

    private static void DeleteOwnedDirectory(string directory)
    {
        try
        {
            var temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            var ownedRoot = Path.GetFullPath(directory);
            var name = Path.GetFileName(ownedRoot);
            if (!string.Equals(Path.GetDirectoryName(ownedRoot), temporaryRoot, StringComparison.OrdinalIgnoreCase)
                || !name.StartsWith(DirectoryPrefix, StringComparison.Ordinal)
                || !Guid.TryParseExact(name[DirectoryPrefix.Length..], "N", out _)) return;
            DeleteContainedDirectory(new DirectoryInfo(ownedRoot), ownedRoot + Path.DirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException) { }
    }

    private static void DeleteContainedDirectory(DirectoryInfo directory, string ownedPrefix)
    {
        if (!directory.Exists) return;
        // Remove directory links themselves; never enumerate or follow their targets.
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) { directory.Delete(); return; }
        foreach (var child in directory.EnumerateFileSystemInfos())
        {
            if (!Path.GetFullPath(child.FullName).StartsWith(ownedPrefix, StringComparison.OrdinalIgnoreCase)) return;
            if (child is DirectoryInfo childDirectory) DeleteContainedDirectory(childDirectory, ownedPrefix);
            else child.Delete();
        }
        directory.Delete();
    }
}
