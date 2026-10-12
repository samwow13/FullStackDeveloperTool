using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using FullStackLauncher.Models;
using Microsoft.Win32;

namespace FullStackLauncher.Services;

/// <summary>Copies the bundled tray agent locally and opens its current-user setup with Windows UAC.</summary>
public sealed class LanLocalCompanionInstaller
{
    private const string AgentName = "FullStackRemoteCompanion";
    private const string AgentFileName = AgentName + ".exe";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public async Task<LanRemoteCommandResult> InstallAsync(string bundledExePath, CancellationToken cancellationToken = default)
    {
        var sourcePath = Path.GetFullPath(bundledExePath);
        if (!Path.GetFileName(sourcePath).Equals(AgentFileName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose the bundled FullStackRemoteCompanion.exe.", nameof(bundledExePath));
        RefuseReparsePath(sourcePath);
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FullStackLauncher", "RemoteCompanion");
        var executable = Path.Combine(directory, AgentFileName);
        RefuseReparsePath(executable);
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("Current Windows user identity is unavailable.");
        using (var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + AgentName))
            if (service is not null)
                throw new InvalidOperationException("A Remote Companion background service already exists. Review that installation before installing a tray agent.");

        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous);
        if (source.Length is < 1 or > 512L * 1024 * 1024)
            throw new InvalidOperationException("The bundled agent package has an invalid size.");
        var expectedHash = await SHA256.HashDataAsync(source, cancellationToken).ConfigureAwait(false);
        source.Position = 0;
        var active = HasActiveAgent(sid);
        var runningHere = IsRunningAt(executable);
        if (active || runningHere)
        {
            var runningHash = File.Exists(executable) ? await HashFileAsync(executable, cancellationToken).ConfigureAwait(false) : null;
            if (!active || !runningHere || !File.Exists(executable)
                || runningHash is null || !CryptographicOperations.FixedTimeEquals(expectedHash, runningHash))
                throw new InvalidOperationException("A companion is already running. Exit that agent through its tray menu before installing this version; no process was stopped or replaced.");
            return new("Install Agent", "Agent already runs from this PC's installation folder. Open its existing Remote Companion tray icon to enable pairing or change startup and firewall settings.\n"
                + $"Path: {executable}", DateTimeOffset.UtcNow);
        }

        var startupCommand = "\"" + executable + "\" --background";
        using (var startup = Registry.CurrentUser.OpenSubKey(RunKey))
        {
            var existing = startup?.GetValue(AgentName);
            if (existing is not null && (existing is not string text || text.Length > 0 && !text.Equals(startupCommand, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("An existing companion startup setting points elsewhere. Review that setting before installing the agent here.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        RefuseReparsePath(executable);
        var temporary = Path.Combine(directory, ".agent-install-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var copy = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous))
            {
                await source.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
                await copy.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            RefuseReparsePath(temporary);
            var copiedHash = await HashFileAsync(temporary, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(expectedHash, copiedHash))
                throw new InvalidOperationException("The local agent copy failed SHA256 verification. No unverified executable was launched.");
            cancellationToken.ThrowIfCancellationRequested();
            RefuseReparsePath(executable);
            if (HasActiveAgent(sid) || IsRunningAt(executable))
                throw new InvalidOperationException("The companion started during installation. Exit it through its tray menu before installing; no running executable was replaced.");
            if (File.Exists(executable))
            {
                // Requesting write access fails for an executing image. Denying read sharing also prevents
                // a new process opening this old executable while its directory entry is replaced.
                using var existing = new FileStream(executable, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete);
                File.Move(temporary, executable, overwrite: true);
            }
            else File.Move(temporary, executable, overwrite: false);
            RefuseReparsePath(executable);
            var installedHash = await HashFileAsync(executable, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(expectedHash, installedHash))
                throw new InvalidOperationException("The installed agent failed SHA256 verification. No unverified executable was launched.");
            cancellationToken.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = directory };
            start.ArgumentList.Add("--setup-installed-agent");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows could not open agent setup.");
            return new("Install Agent", "Agent copied to this PC and setup opened. Complete the Windows administrator approval prompt. After approval, the agent enables Windows sign-in startup and opens its pairing window.\n"
                + $"Path: {executable}\nEnable pairing, then enter the displayed code on the remoting PC and approve its matching fingerprint here. Desktop permission stays under your control.", DateTimeOffset.UtcNow);
        }
        catch (Win32Exception)
        {
            throw new InvalidOperationException("Windows could not open agent setup. Check local Windows policy and try opening the installed agent.");
        }
        catch (IOException ex)
        {
            throw new IOException("The local agent could not be copied or opened. Check folder permissions, free space, and whether the agent is running. Existing startup and firewall settings were not changed by the copy operation.", ex);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary) && (File.GetAttributes(temporary) & FileAttributes.ReparsePoint) == 0)
                    File.Delete(temporary);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task<byte[]> HashFileAsync(string path, CancellationToken token)
    {
        RefuseReparsePath(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous);
        return await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
    }

    private static void RefuseReparsePath(string path)
    {
        if (!Path.IsPathRooted(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidOperationException("Agent installation requires local filesystem paths.");
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Agent installation paths must not use symbolic links or junctions.");
    }

    private static bool HasActiveAgent(string sid)
    {
        if (!Mutex.TryOpenExisting(@"Global\FullStackRemoteCompanion." + sid, out var mutex)) return false;
        using (mutex)
        {
            var acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                return !acquired;
            }
            finally { if (acquired) mutex.ReleaseMutex(); }
        }
    }

    private static bool IsRunningAt(string executable)
    {
        foreach (var process in Process.GetProcessesByName(AgentName))
        {
            using (process)
            {
                try
                {
                    var startedAt = process.StartTime;
                    var path = process.MainModule?.FileName;
                    if (!process.HasExited && process.StartTime == startedAt
                        && path is not null && Path.GetFullPath(path).Equals(executable, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
            }
        }
        return false;
    }
}
