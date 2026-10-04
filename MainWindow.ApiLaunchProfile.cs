using System.Text.Json;
using System.Windows;
using FullStackLauncher.Models;
using FullStackLauncher.Services;
using FullStackLauncher.ViewModels;

namespace FullStackLauncher;

public partial class MainWindow
{
    private bool _apiLaunchProfileEditing;

    private void EditApiLaunchProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ServiceFrom(sender) is not { CanConfigureApi: true } service || SelectedProject is not { } project ||
            _closing || _closed || _closeRequested || _batchBusy || _forceStopBatchBusy ||
            _savingProjectEdits || IsEditing || !Services.Contains(service)) return;
        var originalProfile = JsonSerializer.Serialize(service.Profile);
        var directory = service.Directory;
        var stopRevision = service.Runner.StopRevision;
        // The modal loop still receives dashboard/bridge messages. Use the save gate
        // throughout without exposing or creating unrelated inline project drafts.
        _apiLaunchProfileEditing = true;
        SetSavingProjectEdits(true);
        try
        {
            var editor = new ApiLaunchProfileWindow(service.Name, service.Profile, directory,
                draft => SaveApiLaunchProfileAsync(project, service, originalProfile, directory, stopRevision, draft))
            { Owner = this };
            editor.ShowDialog();
        }
        catch (Exception)
        {
            Notice = "The API launch profile could not be opened. Check the API folder and reopen the launcher.";
        }
        finally
        {
            if (_savingProjectEdits) SetSavingProjectEdits(false);
            _apiLaunchProfileEditing = false;
        }
    }

    private async Task SaveApiLaunchProfileAsync(ProjectProfile project, ServiceViewModel service,
        string originalProfile, string directory, int stopRevision, ApiLaunchProfileDraft draft)
    {
        RequireCurrent();
        ApiLaunchConfiguration.ValidateLaunchCommand(draft.Command);
        if (service.IsBusy || service.IsStopping || _batchBusy || _forceStopBatchBusy)
            throw new InvalidOperationException("Wait for the current service operation before saving. Your draft is retained.");
        await service.Runner.RefreshForProfileEditAsync();
        RequireCurrent();
        service.Update();
        if (service.IsBusy || service.IsStopping || service.Runner.HasManagedProcess ||
            !service.Runner.HasVerifiedNoServiceProcesses || service.Runner.Snapshot.ProcessIds.Count != 0)
            throw new InvalidOperationException("Stop the API and complete a successful process check before saving launch changes. Your draft is retained.");

        // Build a detached candidate at save time; the protected values and legacy
        // command remain untouched. Preserve original profile/runner identities.
        var candidate = JsonSerializer.Deserialize<LauncherSettings>(JsonSerializer.Serialize(_settings))!;
        var saved = candidate.Projects.Single(item => item.Id == project.Id).Services
            .Single(item => item.Id == service.Profile.Id);
        saved.ApiConfiguration = new()
        {
            Environment = "Local",
            LaunchCommand = draft.Command == ApiLaunchConfiguration.DefaultLaunchCommand ? null : draft.Command
        };
        _store.Save(candidate);
        service.Profile.ApiConfiguration = saved.ApiConfiguration;
        service.Runner.ConfigurationNeedsRestart = false;
        service.Update();
        UpdateActions();
        Notice = $"Saved {service.Name} launch profile. Changes apply on the next start; the API was not restarted.";

        void RequireCurrent()
        {
            if (_closing || _closed || _closeRequested || IsEditing || !_apiLaunchProfileEditing || !_savingProjectEdits ||
                !ReferenceEquals(project, SelectedProject) || !_settings.Projects.Contains(project) ||
                !project.Services.Contains(service.Profile) || !Services.Contains(service) ||
                !_runners.TryGetValue(project.Id, out var known) || !known.Contains(service) ||
                service.Runner.StopRevision != stopRevision ||
                !service.Directory.Equals(directory, StringComparison.OrdinalIgnoreCase) ||
                JsonSerializer.Serialize(service.Profile) != originalProfile)
                throw new InvalidOperationException("The API or project changed while its launch profile was open. Reopen the editor before saving.");
        }
    }
}
