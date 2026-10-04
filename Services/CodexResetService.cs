using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32.SafeHandles;

namespace FullStackLauncher.Services;

internal sealed class CodexResetPlan
{
    internal IReadOnlyList<CodexResetProcess> Processes { get; init; } = [];
    internal IReadOnlyList<CodexResetApp> Apps { get; init; } = [];
    internal int SessionId { get; init; }
    internal int ExecutionStarted;
    public IReadOnlyList<string> Issues { get; init; } = [];
    public int ProcessCount => Processes.Count;
    public int DesktopAppCount => Apps.Count;
    public bool CanExecute => Issues.Count == 0 && DesktopAppCount > 0;
    public string ConfirmationText =>
        $"Force close {ProcessCount} Codex/ChatGPT process{(ProcessCount == 1 ? "" : "es")} in this Windows session "
        + $"and reopen {DesktopAppCount} desktop app{(DesktopAppCount == 1 ? "" : "s")}?\n\n"
        + "Unsaved work and running chats may be interrupted. CLI sessions will also stop; their arguments and sessions are not replayed. "
        + "Desktop apps will recreate their own background processes. Other Windows sessions are excluded.";
}

internal sealed record CodexResetResult(bool Succeeded, string Summary, IReadOnlyList<string> Issues);
internal sealed record CodexResetProcess(int Id, int ParentId, string Name, string Executable, long CreationTime);
internal sealed record CodexResetApp(string Name, string Executable, string? PackageFullName,
    string? PackageFamilyName, string? ApplicationId, long FileLength, long LastWriteUtcTicks)
{
    public string Key => PackageFamilyName is { } family && ApplicationId is { } id
        ? family + "!" + id : Executable;
}

/// <summary>
/// Prepares a reviewable reset without changing Codex, then stops only reviewed process identities.
/// Never reads command lines, replays CLI arguments, or terminates process trees.
/// </summary>
internal static partial class CodexResetService
{
    private const string InstalledCodexFamily = "OpenAI.Codex_2p2nqsd0c76g0";
    private const uint QueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const uint Terminate = 0x0001;
    private const uint WaitSignaled = 0;
    private const uint WaitTimeout = 258;
    private const int InsufficientBuffer = 122;
    private const int NoPackage = 15700;
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(5);

    public static Task<CodexResetPlan> PrepareAsync(CancellationToken token = default) =>
        Task.Run(() => Prepare(token), token);

    public static Task<CodexResetResult> ExecuteAsync(CodexResetPlan plan,
        CancellationToken token = default, IProgress<string>? progress = null) =>
        Task.Run(() =>
        {
            // A Windows mutex is thread-owned. Keep acquisition and release on this worker
            // while asynchronous stop/launch waits run without blocking the UI dispatcher.
            using var resetMutex = new Mutex(false, $"Local\\FullStackLauncher.CodexReset.Session{plan.SessionId}");
            var acquired = false;
            try
            {
                try { acquired = resetMutex.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired)
                    return new CodexResetResult(false, "Reset was not started.", ["Another launcher is already resetting Codex. Wait for it to finish."]);
                return ExecuteCoreAsync(plan, token, progress).GetAwaiter().GetResult();
            }
            finally { if (acquired) resetMutex.ReleaseMutex(); }
        }, token);

    private static CodexResetPlan Prepare(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var sessionId = CurrentSessionId();
        var inventory = Capture(sessionId, token);
        var issues = new List<string>(inventory.Issues);
        var apps = new Dictionary<string, CodexResetApp>(StringComparer.OrdinalIgnoreCase);
        var packages = new Dictionary<string, IReadOnlyList<CodexResetApp>>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in inventory.Processes)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var handle = OpenProcess(QueryLimitedInformation | Synchronize, false, (uint)process.Id);
                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != 87) issues.Add(ProcessError(process, "read its app identity", error));
                    continue;
                }
                if (!Matches(handle, process)) continue;
                var packageFullName = ReadPackageFullName(handle);
                if (packageFullName is not null)
                {
                    if (!packages.TryGetValue(packageFullName, out var packageApps))
                    {
                        packageApps = ReadPackageApps(packageFullName);
                        packages.Add(packageFullName, packageApps);
                    }
                    var matches = packageApps.Where(app => PathsEqual(app.Executable, process.Executable)).ToArray();
                    if (matches.Length == 1) apps.TryAdd(matches[0].Key, matches[0]);
                    else if (IsChatGpt(process.Name) || IsGuiExecutable(process.Executable))
                        issues.Add($"Cannot resolve one desktop application for {process.Name} (PID {process.Id}).");
                }
                else if (IsChatGpt(process.Name) || IsGuiExecutable(process.Executable))
                {
                    // WindowsApps executables require registered package activation. Never use
                    // a renderer's executable path as an unpackaged fallback for a packaged app.
                    if (IsWindowsAppsPath(process.Executable))
                    {
                        var packageApp = ReadInstalledCodexApps().SingleOrDefault(app => PathsEqual(app.Executable, process.Executable));
                        if (packageApp is null)
                            issues.Add($"Cannot resolve package activation for {process.Name} (PID {process.Id}).");
                        else apps.TryAdd(packageApp.Key, packageApp);
                    }
                    else
                    {
                        var app = CreateApp(IsChatGpt(process.Name) ? "ChatGPT" : "Codex",
                            process.Executable, null, null, null);
                        apps.TryAdd(app.Key, app);
                    }
                }
            }
            catch (Exception exception) when (IsExpectedFailure(exception))
            {
                issues.Add($"Cannot prepare desktop restart for {process.Name} (PID {process.Id}): {FailureMessage(exception)}");
            }
        }

        // A crashed desktop may have no remaining process. Resolve its installed package
        // before stopping anything; do not start an unused ChatGPT installation.
        if (!apps.Values.Any(app => app.Name.Equals("Codex", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var installedApps = ReadInstalledCodexApps();
                if (installedApps.Count > 1)
                    issues.Add("Multiple Codex desktop applications are registered. Cannot choose one restart target.");
                else if (installedApps.Count == 1)
                    apps.TryAdd(installedApps[0].Key, installedApps[0]);
            }
            catch (Exception exception) when (IsExpectedFailure(exception))
            {
                issues.Add($"Cannot prepare installed Codex restart: {FailureMessage(exception)}");
            }
        }
        if (apps.Count == 0 && issues.Count == 0)
            issues.Add("No restartable Codex or ChatGPT desktop app was found. Open or install Codex, then try again.");

        AddLauncherJobIssue(inventory, issues);
        return new CodexResetPlan
        {
            SessionId = sessionId, Processes = inventory.Processes.ToArray(),
            Apps = apps.Values.ToArray(), Issues = issues.Distinct().ToArray()
        };
    }

    private static async Task<CodexResetResult> ExecuteCoreAsync(CodexResetPlan plan,
        CancellationToken token, IProgress<string>? progress)
    {
        if (!plan.CanExecute) return new(false, "Reset was not started.", plan.Issues);
        if (Interlocked.Exchange(ref plan.ExecutionStarted, 1) != 0)
            return new(false, "Reset was not started.", ["This reset was already used. Prepare a new reset."]);

        var handles = new List<(CodexResetProcess Process, SafeProcessHandle Handle)>();
        var issues = new List<string>();
        var stopped = 0;
        var alreadyExited = 0;
        var reopened = 0;
        var stoppingStarted = false;
        try
        {
            token.ThrowIfCancellationRequested();
            if (CurrentSessionId() != plan.SessionId)
                return new(false, "Reset was not started.", ["The Windows session changed. Prepare a new reset."]);

            progress?.Report("Checking reviewed processes and restart targets…");
            var current = Capture(plan.SessionId, token);
            issues.AddRange(current.Issues);
            AddLauncherJobIssue(current, issues);
            if (current.Processes.Any(process => !plan.Processes.Any(reviewed => SameIdentity(process, reviewed))))
                issues.Add("New Codex/ChatGPT processes appeared since review. Prepare a new reset before stopping them.");
            foreach (var app in plan.Apps) ValidateApp(app);
            if (issues.Count > 0) return new(false, "Reset was not started.", issues.Distinct().ToArray());

            // Acquire and verify every termination handle before the first destructive step.
            // A handle remains tied to its process object even if its numeric PID is reused.
            foreach (var process in plan.Processes)
            {
                token.ThrowIfCancellationRequested();
                var handle = OpenProcess(QueryLimitedInformation | Synchronize | Terminate, false, (uint)process.Id);
                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    if (error == 87) alreadyExited++;
                    else issues.Add(ProcessError(process, "open it for termination", error));
                    continue;
                }
                try
                {
                    if (WaitForSingleObject(handle, 0) == WaitSignaled || !Matches(handle, process))
                    {
                        // A different PID identity is never terminated. The fresh inventory
                        // above has already rejected unreviewed matching replacements.
                        alreadyExited++;
                        handle.Dispose();
                        continue;
                    }
                    if (!ProcessIdToSessionId((uint)process.Id, out var session) || session != plan.SessionId)
                    {
                        issues.Add($"Cannot verify the Windows session for {process.Name} (PID {process.Id}).");
                        handle.Dispose();
                        continue;
                    }
                    handles.Add((process, handle));
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
            }
            if (issues.Count > 0) return new(false, "Reset was not started.", issues.Distinct().ToArray());
            token.ThrowIfCancellationRequested();
            stoppingStarted = true;
            progress?.Report("Stopping reviewed Codex/ChatGPT processes…");
            var reviewedById = plan.Processes.ToDictionary(process => process.Id);
            foreach (var (process, handle) in handles
                .OrderBy(item => plan.Apps.Any(app => PathsEqual(app.Executable, item.Process.Executable)) ? 0 : 1)
                .ThenBy(item => ReviewedDepth(item.Process, reviewedById)))
            {
                if (WaitForSingleObject(handle, 0) == WaitSignaled) continue;
                if (!TerminateProcess(handle, 1))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (WaitForSingleObject(handle, 0) != WaitSignaled)
                        issues.Add(ProcessError(process, "stop it", error));
                }
            }

            // Finish stop verification and recovery even if the UI closes after termination.
            // Cancellation can stop preparation but cannot abandon a half-finished reset.
            var wait = Stopwatch.StartNew();
            while (wait.Elapsed < StopTimeout && handles.Any(item => WaitForSingleObject(item.Handle, 0) == WaitTimeout))
                await Task.Delay(100).ConfigureAwait(false);
            foreach (var (process, handle) in handles)
            {
                var status = WaitForSingleObject(handle, 0);
                if (status == WaitSignaled) stopped++;
                else issues.Add(status == WaitTimeout
                    ? $"{process.Name} (PID {process.Id}) did not exit within {StopTimeout.TotalSeconds:0} seconds."
                    : ProcessError(process, "verify its exit", Marshal.GetLastWin32Error()));
            }
            var remaining = Capture(plan.SessionId, CancellationToken.None);
            issues.AddRange(remaining.Issues);
            if (remaining.Processes.Count > 0)
                issues.Add($"{remaining.Processes.Count} Codex/ChatGPT process(es) are still running or appeared during reset. Apps were not reopened.");
            if (issues.Count > 0)
                return new(false, $"Reset incomplete. Confirmed {stopped + alreadyExited} reviewed processes exited; apps were not reopened.", issues.Distinct().ToArray());

            progress?.Report("Reopening desktop apps…");
            foreach (var app in plan.Apps)
            {
                try
                {
                    ValidateApp(app);
                    var activatedId = LaunchApp(app);
                    if (!await ConfirmLaunchAsync(app, activatedId, plan.SessionId).ConfigureAwait(false))
                        issues.Add($"{app.Name} launch was requested, but its running desktop process could not be confirmed.");
                    else reopened++;
                }
                catch (Exception exception) when (IsExpectedFailure(exception))
                {
                    issues.Add($"Could not reopen {app.Name}: {FailureMessage(exception)}");
                }
            }
            return issues.Count == 0
                ? new(true, $"Reopened {reopened} desktop app{(reopened == 1 ? "" : "s")}; {stopped + alreadyExited} reviewed processes exited.", [])
                : new(false, $"Reset incomplete. {stopped + alreadyExited} reviewed processes exited; reopened {reopened} of {plan.DesktopAppCount} desktop apps.", issues.Distinct().ToArray());
        }
        catch (OperationCanceledException) when (!stoppingStarted && token.IsCancellationRequested)
        {
            return new(false, "Reset canceled before stopping processes.", []);
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            return new(false, stoppingStarted ? "Reset incomplete." : "Reset was not started.", [FailureMessage(exception)]);
        }
        finally
        {
            foreach (var (_, handle) in handles) handle.Dispose();
        }
    }

    private static async Task<bool> ConfirmLaunchAsync(CodexResetApp app, uint activatedId, int sessionId)
    {
        var wait = Stopwatch.StartNew();
        await Task.Delay(300).ConfigureAwait(false);
        while (wait.Elapsed < LaunchTimeout)
        {
            var inventory = Capture(sessionId, CancellationToken.None);
            if (inventory.Issues.Count == 0)
            {
                foreach (var process in inventory.Processes.Where(process => PathsEqual(process.Executable, app.Executable)))
                {
                    using var handle = OpenProcess(QueryLimitedInformation | Synchronize, false, (uint)process.Id);
                    if (handle.IsInvalid || !Matches(handle, process)) continue;
                    if (app.PackageFamilyName is { } family)
                    {
                        var package = ReadPackageFullName(handle);
                        if (package is null || !PackageFamily(package).Equals(family, StringComparison.OrdinalIgnoreCase)) continue;
                    }
                    // Electron may hand off from the activated PID to a new desktop process.
                    // Exact executable and package identity still establish a running restart.
                    if (activatedId != 0) return true;
                }
            }
            await Task.Delay(150).ConfigureAwait(false);
        }
        return false;
    }

    private static uint LaunchApp(CodexResetApp app)
    {
        if (app.PackageFamilyName is not null && app.ApplicationId is not null)
        {
            object? activation = null;
            try
            {
                var type = Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"), throwOnError: true)!;
                activation = Activator.CreateInstance(type)!;
                var manager = (IApplicationActivationManager)activation;
                var result = manager.ActivateApplication(app.Key, null, 0, out var id);
                Marshal.ThrowExceptionForHR(result);
                if (id == 0) throw new InvalidOperationException("Windows did not return a desktop process identity.");
                return id;
            }
            finally
            {
                if (activation is not null && Marshal.IsComObject(activation)) Marshal.FinalReleaseComObject(activation);
            }
        }
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = app.Executable, WorkingDirectory = Path.GetDirectoryName(app.Executable)!, UseShellExecute = false
        }) ?? throw new InvalidOperationException("Windows did not return a desktop process identity.");
        return checked((uint)process.Id);
    }

    private static void ValidateApp(CodexResetApp app)
    {
        var current = app.PackageFullName is not null
            ? ReadPackageApps(app.PackageFullName).SingleOrDefault(candidate => candidate.Key.Equals(app.Key, StringComparison.OrdinalIgnoreCase))
            : CreateApp(app.Name, app.Executable, null, null, null);
        if (current is null || !PathsEqual(current.Executable, app.Executable)
            || current.FileLength != app.FileLength || current.LastWriteUtcTicks != app.LastWriteUtcTicks)
            throw new InvalidOperationException($"{app.Name} installation changed since review. Prepare a new reset.");
    }

    private static CodexResetApp CreateApp(string name, string executable, string? packageFullName,
        string? family, string? applicationId)
    {
        var file = new FileInfo(executable);
        if (!file.Exists) throw new InvalidOperationException($"{name} desktop executable is unavailable.");
        return new(name, file.FullName, packageFullName, family, applicationId, file.Length, file.LastWriteTimeUtc.Ticks);
    }

    private static IReadOnlyList<CodexResetApp> ReadInstalledCodexApps()
    {
        var packages = RegisteredPackages(InstalledCodexFamily);
        // Only current-user registered versions participate. Prefer the newest installed
        // version, then keep one activation target per manifest application.
        foreach (var package in packages.OrderByDescending(PackageVersion))
        {
            var apps = ReadPackageApps(package).Where(app => app.Name.Equals("Codex", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (apps.Length > 0) return apps;
        }
        return [];
    }

    private static Version PackageVersion(string fullName)
    {
        var parts = fullName.Split('_');
        return parts.Length > 1 && Version.TryParse(parts[1], out var version) ? version : new Version();
    }

    private static IReadOnlyList<CodexResetApp> ReadPackageApps(string packageFullName)
    {
        var packagePath = PackagePath(packageFullName);
        var family = PackageFamily(packageFullName);
        using var stream = new FileStream(Path.Combine(packagePath, "AppxManifest.xml"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024
        });
        var document = XDocument.Load(reader);
        var name = packageFullName.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) ? "Codex" : "ChatGPT";
        var rootPrefix = Path.GetFullPath(packagePath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var apps = new List<CodexResetApp>();
        foreach (var application in document.Descendants().Where(element => element.Name.LocalName == "Application"))
        {
            var id = (string?)application.Attribute("Id");
            var relative = (string?)application.Attribute("Executable");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) continue;
            var executable = Path.GetFullPath(Path.Combine(packagePath, relative));
            if (!executable.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) || !IsTargetName(Path.GetFileName(executable))) continue;
            apps.Add(CreateApp(name, executable, packageFullName, family, id));
        }
        return apps.DistinctBy(app => app.Key, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsGuiExecutable(string executable)
    {
        using var stream = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 64 || reader.ReadUInt16() != 0x5A4D) return false;
        stream.Position = 0x3C;
        var headerOffset = reader.ReadInt32();
        if (headerOffset < 64 || (long)headerOffset + 94 > stream.Length) return false;
        stream.Position = headerOffset;
        if (reader.ReadUInt32() != 0x00004550) return false;
        stream.Position = headerOffset + 24;
        var magic = reader.ReadUInt16();
        if (magic is not (0x10B or 0x20B)) return false;
        stream.Position = headerOffset + 24 + 68;
        return reader.ReadUInt16() == 2;
    }

    private static bool IsWindowsAppsPath(string path) => path.Split(Path.DirectorySeparatorChar)
        .Any(part => part.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase));
    private static bool IsChatGpt(string name) => name.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase);
    private static bool IsTargetName(string name) => IsChatGpt(name) || name.Equals("Codex.exe", StringComparison.OrdinalIgnoreCase);
    private static bool PathsEqual(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);
    private static bool SameIdentity(CodexResetProcess left, CodexResetProcess right) =>
        left.Id == right.Id && left.CreationTime == right.CreationTime && PathsEqual(left.Executable, right.Executable);
    private static int ReviewedDepth(CodexResetProcess process, IReadOnlyDictionary<int, CodexResetProcess> byId)
    {
        var seen = new HashSet<int> { process.Id };
        var depth = 0;
        while (byId.TryGetValue(process.ParentId, out var parent) && seen.Add(parent.Id))
        {
            depth++;
            process = parent;
        }
        return depth;
    }
    private static bool IsExpectedFailure(Exception exception) => exception is Win32Exception or IOException
        or UnauthorizedAccessException or InvalidOperationException or ArgumentException or XmlException or COMException
        or System.Security.SecurityException or NotSupportedException;
    private static string FailureMessage(Exception exception) => exception is Win32Exception win32
        ? $"Windows error {win32.NativeErrorCode}: {win32.Message}"
        : exception is COMException com ? $"Windows activation error 0x{com.HResult:X8}."
        : exception is IOException or UnauthorizedAccessException or System.Security.SecurityException
            ? "Windows could not read the desktop installation."
            : exception is XmlException ? "The installed application manifest could not be read."
            : exception.Message;
    private static string ProcessError(CodexResetProcess process, string action, int error) =>
        $"Could not {action} for {process.Name} (PID {process.Id}). Windows error {error}: {new Win32Exception(error).Message}";
}
