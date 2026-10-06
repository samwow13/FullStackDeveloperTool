using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Threading;

namespace FullStackLauncher.Services;

/// <summary>
/// A dashboard-owned, process-lifetime permit for queue starts. The saved power
/// preference alone cannot authorize work after the dashboard closes or crashes.
/// Separate leases allow multiple dashboards without one disabling another.
/// </summary>
internal sealed class QueueDashboardPermit(string settingsPath, Dispatcher dispatcher) : IDisposable
{
    private readonly string _scope = ScopeFor(settingsPath);
    private Mutex? _mutex;
    private FileStream? _lease;

    internal void SetEnabled(bool enabled)
    {
        dispatcher.VerifyAccess(); // Mutex ownership stays on the dashboard thread.
        if (!enabled) { Dispose(); return; }
        if (_mutex != null) return;
        var id = Guid.NewGuid().ToString("N");
        var mutex = new Mutex(true, MutexName(_scope, id));
        try
        {
            var folder = LeaseFolder(_scope);
            Directory.CreateDirectory(folder);
            _lease = new FileStream(Path.Combine(folder, id + ".permit"), FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.DeleteOnClose);
            _mutex = mutex;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
            throw new InvalidOperationException("Long Running Task could not enable queue starts. Check access to the launcher runtime folder and try again.", ex);
        }
    }

    internal static bool IsEnabled(string settingsPath)
    {
        var scope = ScopeFor(settingsPath);
        var folder = LeaseFolder(scope);
        try
        {
            if (!Directory.Exists(folder)) return false;
            foreach (var file in Directory.EnumerateFiles(folder, "*.permit").Take(128))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                if (!Guid.TryParseExact(id, "N", out _) ||
                    !Mutex.TryOpenExisting(MutexName(scope, id), out var mutex)) continue;
                using (mutex)
                {
                    bool acquired;
                    try { acquired = mutex.WaitOne(0); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) return true;
                    mutex.ReleaseMutex(); // Closed/crashed dashboard: no live permit.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or WaitHandleCannotBeOpenedException)
        {
            return false; // Unreadable state never authorizes a queue start.
        }
        return false;
    }

    public void Dispose()
    {
        dispatcher.VerifyAccess();
        if (_mutex != null)
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
            _mutex = null;
        }
        _lease?.Dispose();
        _lease = null;
    }

    private static string ScopeFor(string path) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())));
    private static string LeaseFolder(string scope) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FullStackLauncher", "queue-dashboard-permits", scope);
    private static string MutexName(string scope, string id) =>
        @"Local\FullStackLauncher.QueueDashboard." + scope + "." + id;
}
