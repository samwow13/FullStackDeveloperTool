using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using FullStackLauncher.Models;
using FullStackLauncher.Services;
using FullStackLauncher.ViewModels;

namespace FullStackLauncher;

internal enum ServiceEditorDraftKind { ApiLaunchProfile, ServicePort, ChangeDatabase }
internal sealed record ApiLaunchProfileEditorResume(string Command, string BaselineCommand, int CaretIndex);
internal sealed record ServicePortEditorResume(string Port, string BaselinePort, int CaretIndex);
internal sealed record ChangeDatabaseEditorResume(string? ConnectionKey, string Value, string BaselineValue,
    string? SnapshotFingerprint, bool RetryRequired, bool ConfigurationSaved, bool ReviewRequired);
internal sealed record ServiceEditorWindowBounds(double Left, double Top, double Width, double Height);
internal sealed record ServiceEditorDraftCheckpoint(string CheckpointId, ServiceEditorDraftKind Kind, string ProjectId, string ServiceId,
    string Directory, string ProfileFingerprint, ServiceEditorWindowBounds Bounds,
    ApiLaunchProfileEditorResume? LaunchProfile = null, ServicePortEditorResume? Port = null,
    ChangeDatabaseEditorResume? Database = null);

public partial class MainWindow
{
    private const string ServiceEditorResumeSection = "service-editors";
    private readonly Dictionary<Window, (ServiceEditorDraftKind Kind, ProjectProfile Project, ServiceViewModel Service, string? CheckpointId)> _serviceEditorResumeBindings = [];

    private void RegisterServiceEditorResume(Window editor, ProjectProfile project, ServiceViewModel service)
    {
        var kind = editor switch
        {
            ApiLaunchProfileWindow => ServiceEditorDraftKind.ApiLaunchProfile,
            ServicePortWindow => ServiceEditorDraftKind.ServicePort,
            ChangeDatabaseWindow => ServiceEditorDraftKind.ChangeDatabase,
            _ => throw new InvalidOperationException("Unsupported service draft editor.")
        };
        ServiceEditorDraftCheckpoint[] candidates;
        try
        {
            candidates = ReadServiceEditorCheckpoints().Where(state => SameServiceEditorIdentity(state, kind, project.Id, service.Profile.Id)).ToArray();
        }
        catch
        {
            // This fresh window has no restored draft yet. Release it if checkpoint
            // loading fails before ShowDialog, while preserving the saved file.
            editor.Close();
            throw;
        }
        var profileFingerprint = ServiceEditorProfileFingerprint(service.Profile);
        var saved = candidates.FirstOrDefault(state => SameServiceEditorDirectory(state.Directory, service.Directory) &&
            state.ProfileFingerprint == profileFingerprint);
        if (saved is null && candidates.Length > 0)
        {
            Notice = "Saved service editor draft retained. Its service configuration or folder changed; restore that configuration before recovering the draft.";
        }
        _serviceEditorResumeBindings[editor] = (kind, project, service, saved?.CheckpointId);
        editor.Closed += (_, _) => _serviceEditorResumeBindings.Remove(editor);
        void Clear()
        {
            if (_serviceEditorResumeBindings.TryGetValue(editor, out var active) && active.CheckpointId is { } checkpointId)
                ClearServiceEditorCheckpoint(checkpointId);
        }
        switch (editor)
        {
            case ApiLaunchProfileWindow launch:
                launch.ConfigureDashboardResume(saved?.LaunchProfile, Clear);
                break;
            case ServicePortWindow port:
                port.ConfigureDashboardResume(saved?.Port, Clear);
                break;
            case ChangeDatabaseWindow database:
                database.ConfigureDashboardResume(saved?.Database, Clear);
                break;
        }
        if (saved is not null) RestoreServiceEditorBounds(editor, saved.Bounds);
    }

    private void SaveServiceEditorResume(Window dialog)
    {
        if (!_serviceEditorResumeBindings.TryGetValue(dialog, out var binding)) return;
        if (dialog is IDashboardReplacementWindow { IsDashboardReplacementReady: false })
            throw new IOException("Service editor work has not finished. Its draft was preserved; retry Launcher replacement when the current operation completes.");
        var bounds = dialog.WindowState == WindowState.Normal
            ? new Rect(dialog.Left, dialog.Top, dialog.Width, dialog.Height) : dialog.RestoreBounds;
        var checkpointId = binding.CheckpointId ?? Guid.NewGuid().ToString("N");
        // Keep the same identity when a failed checkpoint write is retried.
        _serviceEditorResumeBindings[dialog] = (binding.Kind, binding.Project, binding.Service, checkpointId);
        var state = new ServiceEditorDraftCheckpoint(checkpointId, binding.Kind, binding.Project.Id, binding.Service.Profile.Id,
            binding.Service.Directory, ServiceEditorProfileFingerprint(binding.Service.Profile),
            new(bounds.Left, bounds.Top, bounds.Width, bounds.Height));
        state = dialog switch
        {
            ApiLaunchProfileWindow launch => state with { LaunchProfile = launch.CaptureDashboardResume() },
            ServicePortWindow port => state with { Port = port.CaptureDashboardResume() },
            ChangeDatabaseWindow database => state with { Database = database.CaptureDashboardResume() },
            _ => state
        };
        var saved = ReadServiceEditorCheckpoints().Where(existing => existing.CheckpointId != checkpointId).Append(state).ToArray();
        if (saved.Length > 128)
            throw new IOException("Too many saved service editor drafts remain unresolved. Existing drafts were preserved; resolve their original editors before retrying Launcher replacement.");
        _dashboardResumeStore.Save(ServiceEditorResumeSection, saved);
        // Normal discard guards change only after the encrypted write succeeds.
        switch (dialog)
        {
            case ApiLaunchProfileWindow launch: launch.MarkDashboardResumeCheckpointed(); break;
            case ServicePortWindow port: port.MarkDashboardResumeCheckpointed(); break;
            case ChangeDatabaseWindow database: database.MarkDashboardResumeCheckpointed(); break;
        }
    }

    private void ResumeServiceEditors()
    {
        var saved = ReadServiceEditorCheckpoints();
        foreach (var state in saved)
        {
            if (_closed || _closing || _closeRequested) return;
            var project = Projects.FirstOrDefault(item => item.Id == state.ProjectId);
            var service = project is null ? null : GetRunners(project).FirstOrDefault(item => item.Profile.Id == state.ServiceId);
            if (project is null || service is null || !SameServiceEditorDirectory(state.Directory, service.Directory) ||
                state.ProfileFingerprint != ServiceEditorProfileFingerprint(service.Profile))
            {
                Notice = "Saved service editor draft retained. Its project, service or configuration changed while Launcher restarted.";
                continue;
            }
            if (!ReferenceEquals(project, SelectedProject) || !Services.Contains(service) || IsEditing || _savingProjectEdits)
            {
                Notice = "Saved service editor draft retained. Select its project and reopen the service editor to continue.";
                continue;
            }
            // Reuse normal open paths so callbacks capture fresh profiles, stop revisions
            // and current service identities. Opening never saves or runs a command.
            var sender = new System.Windows.Controls.Border { DataContext = service };
            switch (state.Kind)
            {
                case ServiceEditorDraftKind.ApiLaunchProfile:
                    EditApiLaunchProfile_Click(sender, new RoutedEventArgs());
                    break;
                case ServiceEditorDraftKind.ServicePort:
                    ServiceSettingsChangePort_Click(sender, new RoutedEventArgs());
                    break;
                case ServiceEditorDraftKind.ChangeDatabase:
                    ChangeDatabase_Click(sender, new RoutedEventArgs());
                    break;
            }
        }
    }

    private ServiceEditorDraftCheckpoint[] ReadServiceEditorCheckpoints()
    {
        var states = _dashboardResumeStore.Load<ServiceEditorDraftCheckpoint[]>(ServiceEditorResumeSection) ?? [];
        if (states.Length > 128 || states.Any(state => state is null || !Guid.TryParseExact(state.CheckpointId, "N", out _) || !Enum.IsDefined(state.Kind) ||
            string.IsNullOrWhiteSpace(state.ProjectId) || string.IsNullOrWhiteSpace(state.ServiceId) ||
            string.IsNullOrWhiteSpace(state.Directory) || string.IsNullOrWhiteSpace(state.ProfileFingerprint) || state.Bounds is null ||
            state.Kind == ServiceEditorDraftKind.ApiLaunchProfile && state.LaunchProfile is null ||
            state.Kind == ServiceEditorDraftKind.ServicePort && state.Port is null ||
            state.Kind == ServiceEditorDraftKind.ChangeDatabase && state.Database is null))
            throw new IOException("Saved service editor drafts could not be restored safely. Checkpoint files were preserved.");
        return states;
    }

    private void ClearServiceEditorCheckpoint(string checkpointId)
    {
        var states = ReadServiceEditorCheckpoints();
        var retained = states.Where(state => state.CheckpointId != checkpointId).ToArray();
        if (retained.Length == states.Length) return;
        if (retained.Length == 0) _dashboardResumeStore.Remove(ServiceEditorResumeSection);
        else _dashboardResumeStore.Save(ServiceEditorResumeSection, retained);
    }

    private static bool SameServiceEditorIdentity(ServiceEditorDraftCheckpoint state, ServiceEditorDraftKind kind,
        string projectId, string serviceId) => state.Kind == kind && state.ProjectId == projectId && state.ServiceId == serviceId;

    private static bool SameServiceEditorDirectory(string first, string second)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)).Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return false; }
    }

    private static string ServiceEditorProfileFingerprint(ServiceProfile profile) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(profile))));

    private static void RestoreServiceEditorBounds(Window editor, ServiceEditorWindowBounds bounds)
    {
        if (!double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Top) || !double.IsFinite(bounds.Width) ||
            !double.IsFinite(bounds.Height)) return;
        if (bounds.Width >= editor.MinWidth) editor.Width = Math.Min(bounds.Width, SystemParameters.VirtualScreenWidth);
        if (bounds.Height >= editor.MinHeight) editor.Height = Math.Min(bounds.Height, SystemParameters.VirtualScreenHeight);
        if (bounds.Left + editor.Width <= SystemParameters.VirtualScreenLeft ||
            bounds.Left >= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth ||
            bounds.Top + editor.Height <= SystemParameters.VirtualScreenTop ||
            bounds.Top >= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight) return;
        editor.WindowStartupLocation = WindowStartupLocation.Manual;
        editor.Left = bounds.Left;
        editor.Top = bounds.Top;
    }
}
