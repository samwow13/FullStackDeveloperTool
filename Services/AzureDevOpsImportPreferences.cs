using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FullStackLauncher.Services;

/// <summary>Remembers only the last successfully connected, credential-free Azure DevOps project URL.</summary>
public static class AzureDevOpsImportPreferences
{
    private const int Version = 1;
    private const int FileLimit = 8192;
    private static readonly object SyncRoot = new();

    public static string? LoadLastProjectUrl()
    {
        lock (SyncRoot)
        {
            var path = GetPreferencesPath();
            try { return ReadLastProjectUrl(path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                throw new InvalidOperationException(
                    "The saved Azure DevOps URL could not be read. Enter the project URL to connect; the saved file remains unchanged.");
            }
        }
    }

    // Call only after the current URL has completed a successful provider connection.
    public static void SaveLastProjectUrl(string projectUrl)
    {
        var normalizedUrl = AzureDevOpsImportService.ParseProjectUrl(projectUrl).ProjectUrl;
        lock (SyncRoot)
        {
            var path = GetPreferencesPath();
            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                // Serialize saves across launcher processes without creating another data file.
                var mutexName = "Local\\FullStackLauncher.AzureDevOpsImport." +
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
                using var mutex = new Mutex(false, mutexName);
                var acquired = false;
                try
                {
                    try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired)
                        throw new InvalidOperationException(
                            "The Azure DevOps URL is being saved by another launcher. The connection is ready; retry connecting to remember its URL.");

                    // Never replace an unreadable, malformed, or unsupported existing preference.
                    var existingUrl = ReadLastProjectUrl(path);
                    if (existingUrl == normalizedUrl) return;
                    var content = JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        version = Version,
                        lastProjectUrl = normalizedUrl
                    });
                    if (content.Length > FileLimit) throw InvalidSavedUrl();
                    using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                        FileShare.None, 4096, FileOptions.WriteThrough))
                    {
                        stream.Write(content);
                        stream.Flush(flushToDisk: true);
                    }
                    if (ReadLastProjectUrl(path) != existingUrl)
                        throw new InvalidOperationException(
                            "The saved Azure DevOps URL changed during this connection. The connection is ready; reconnect to remember its URL.");
                    if (existingUrl is null) File.Move(temporaryPath, path);
                    else File.Replace(temporaryPath, path, destinationBackupFileName: null);
                }
                finally
                {
                    if (acquired) mutex.ReleaseMutex();
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                throw new InvalidOperationException(
                    "The Azure DevOps URL could not be saved. The connection is ready; check local folder access, then reconnect to remember its URL.");
            }
            finally
            {
                try { File.Delete(temporaryPath); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
        }
    }

    private static string GetPreferencesPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData) || !Path.IsPathFullyQualified(localAppData))
            throw new InvalidOperationException(
                "The Windows user data folder is unavailable. Enter the project URL to connect; it cannot be remembered until the user profile is available.");
        return Path.Combine(localAppData, "FullStackLauncher", "azure-devops-import.json");
    }

    private static string? ReadLastProjectUrl(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw InvalidSavedUrl();

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is 0 or > FileLimit) throw InvalidSavedUrl();
        var content = new byte[FileLimit + 1];
        var count = 0;
        while (count < content.Length)
        {
            var read = stream.Read(content, count, content.Length - count);
            if (read == 0) break;
            count += read;
        }
        if (count is 0 or > FileLimit) throw InvalidSavedUrl();

        try
        {
            using var document = JsonDocument.Parse(content.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw InvalidSavedUrl();
            var hasVersion = false;
            string? storedUrl = null;
            foreach (var property in root.EnumerateObject())
            {
                if (property.NameEquals("version") && !hasVersion &&
                    property.Value.ValueKind == JsonValueKind.Number &&
                    property.Value.TryGetInt32(out var version) && version == Version)
                {
                    hasVersion = true;
                }
                else if (property.NameEquals("lastProjectUrl") && storedUrl is null &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    storedUrl = property.Value.GetString();
                }
                else throw InvalidSavedUrl();
            }
            if (!hasVersion || storedUrl is null ||
                AzureDevOpsImportService.ParseProjectUrl(storedUrl).ProjectUrl != storedUrl)
                throw InvalidSavedUrl();
            return storedUrl;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw InvalidSavedUrl();
        }
    }

    private static InvalidOperationException InvalidSavedUrl() => new(
        "The saved Azure DevOps URL file is invalid or unsupported. Enter the project URL to connect; the saved file remains unchanged. Move the file aside before remembering another URL.");
}
