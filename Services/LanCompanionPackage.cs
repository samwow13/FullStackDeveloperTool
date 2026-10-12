using System.IO;

namespace FullStackLauncher.Services;

/// <summary>Stages only the generic companion bundled with this launcher build.</summary>
internal sealed class LanCompanionPackage : IDisposable
{
    private readonly string _directory;
    public string ExecutablePath { get; }

    private LanCompanionPackage(string directory)
    {
        _directory = directory;
        ExecutablePath = Path.Combine(directory, "FullStackRemoteCompanion.exe");
    }

    public static async Task<LanCompanionPackage> StageAsync(CancellationToken token)
    {
        await using var resource = typeof(LanCompanionPackage).Assembly.GetManifestResourceStream(
            "FullStackLauncher.RemoteCompanion.exe")
            ?? throw new InvalidOperationException("This launcher build does not contain the agent installer. Publish the current launcher before installing an agent.");
        var package = new LanCompanionPackage(Path.Combine(Path.GetTempPath(), "FullStackLauncher-Agent-" + Guid.NewGuid().ToString("N")));
        try
        {
            Directory.CreateDirectory(package._directory);
            if ((File.GetAttributes(package._directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Agent staging folder must not be a link.");
            await using (var destination = new FileStream(package.ExecutablePath, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous))
            {
                await resource.CopyToAsync(destination, token).ConfigureAwait(false);
                await destination.FlushAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            return package;
        }
        catch
        {
            package.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        try
        {
            if (!Directory.Exists(_directory) || (File.GetAttributes(_directory) & FileAttributes.ReparsePoint) != 0) return;
            if (File.Exists(ExecutablePath) && (File.GetAttributes(ExecutablePath) & FileAttributes.ReparsePoint) == 0)
                File.Delete(ExecutablePath);
            Directory.Delete(_directory, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
