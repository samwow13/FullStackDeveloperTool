using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace FullStackLauncher.Services;

/// <summary>Collection-scoped PATs encrypted for this Windows user; never launcher settings or Git configuration.</summary>
public static class AzureDevOpsTokenStore
{
    private const int MaximumBytes = 128 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static bool HasSavedToken(string projectUrl)
    {
        var path = TokenPath(projectUrl);
        try { return File.GetAttributes(path).HasFlag(FileAttributes.Directory) ? throw StoreUnavailable() : true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (Exception exception) when (IsFileError(exception)) { throw StoreUnavailable(); }
    }

    internal static string Read(string projectUrl)
    {
        var scope = TokenScope(projectUrl);
        byte[]? plaintext = null;
        try
        {
            var ciphertext = ReadBounded(TokenPathForScope(scope));
            if (ciphertext is null)
                throw new InvalidOperationException("No Azure DevOps token is saved for this collection. Open Connect with Azure DevOps Token and save a token first.");
            plaintext = WindowsUserSecretProtection.Unprotect(ciphertext, Purpose(scope));
            var token = StrictUtf8.GetString(plaintext);
            ValidateToken(token);
            return token;
        }
        catch (Exception exception) when (IsFileError(exception)) { throw StoreUnavailable(); }
        finally { if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext); }
    }

    internal static void Save(string projectUrl, string token)
    {
        ValidateToken(token);
        var scope = TokenScope(projectUrl);
        var plaintext = StrictUtf8.GetBytes(token);
        try
        {
            var ciphertext = WindowsUserSecretProtection.Protect(plaintext, Purpose(scope));
            WriteAtomic(TokenPathForScope(scope), ciphertext);
            GitConnectionWarmup.InvalidateAzureTokenScope(scope);
            ProjectGitService.Shared.InvalidateAzureTokenScope(scope);
        }
        catch (Exception exception) when (IsFileError(exception)) { throw StoreUnavailable(); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public static void ForgetToken(string projectUrl)
    {
        var scope = TokenScope(projectUrl);
        try
        {
            var path = TokenPathForScope(scope);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var writeLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            File.Delete(path);
            GitConnectionWarmup.InvalidateAzureTokenScope(scope);
            ProjectGitService.Shared.InvalidateAzureTokenScope(scope);
        }
        catch (Exception exception) when (IsFileError(exception)) { throw StoreUnavailable(); }
    }

    public static string LoadLastProjectUrl()
    {
        try
        {
            var bytes = ReadBounded(Path.Combine(StoreFolder(), "last-project.txt"));
            return bytes is null ? "" : AzureDevOpsImportService.ParseProjectUrl(StrictUtf8.GetString(bytes)).ProjectUrl;
        }
        catch (Exception exception) when (IsFileError(exception) || exception is InvalidOperationException) { return ""; }
    }

    public static void SaveLastProjectUrl(string projectUrl)
    {
        var clean = AzureDevOpsImportService.ParseProjectUrl(projectUrl).ProjectUrl;
        try { WriteAtomic(Path.Combine(StoreFolder(), "last-project.txt"), StrictUtf8.GetBytes(clean)); }
        catch (Exception exception) when (IsFileError(exception))
        { throw new InvalidOperationException("The token connection succeeded, but its project URL could not be remembered. Check Windows user folder access."); }
    }

    internal static void ValidateToken(string token)
    {
        // Accept legacy and current PAT formats without imposing a format that Azure may change.
        if (token.Length is < 20 or > 4096 || token.Any(char.IsWhiteSpace) || token.Any(char.IsControl))
            throw new InvalidOperationException("Enter an Azure DevOps personal access token without whitespace. Code Read permits cloning; Code Read & write is needed for pushing.");
    }

    // Hosted organizations retain their existing key and DPAPI purpose. Server collections
    // include the canonical HTTPS origin, port and collection path to isolate credentials.
    private static string TokenScope(string projectUrl) => AzureDevOpsImportService.ParseProjectUrl(projectUrl).TokenScope;
    private static string Purpose(string scope) => "FullStackLauncher.AzureDevOpsToken.v1:" + scope;
    private static string TokenPath(string projectUrl) => TokenPathForScope(TokenScope(projectUrl));
    private static string TokenPathForScope(string scope) => Path.Combine(StoreFolder(),
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope))) + ".dat");

    private static string StoreFolder()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local) || !Path.IsPathFullyQualified(local)) throw StoreUnavailable();
        return Path.Combine(local, "FullStackLauncher", "azure-devops-tokens");
    }

    private static byte[]? ReadBounded(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is < 1 or > MaximumBytes) throw StoreUnavailable();
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) throw StoreUnavailable();
            return bytes;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        using var writeLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, destinationBackupFileName: null);
            else File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception exception) when (IsFileError(exception)) { }
        }
    }

    private static bool IsFileError(Exception exception) => exception is IOException or UnauthorizedAccessException
        or CryptographicException or DecoderFallbackException or System.Security.SecurityException;

    private static InvalidOperationException StoreUnavailable() => new(
        "The saved Azure DevOps token could not be read, unlocked, or changed for this Windows user. Check folder access, or explicitly forget and replace the saved token.");
}
