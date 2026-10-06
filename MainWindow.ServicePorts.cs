using System.Text.Json;
using System.Windows;
using FullStackLauncher.Models;
using FullStackLauncher.Services;
using FullStackLauncher.ViewModels;

namespace FullStackLauncher;

public partial class MainWindow
{
    private bool _servicePortEditing;

    private void ServiceSettingsChangePort_Click(object sender, RoutedEventArgs e)
    {
        CloseServiceMenu(sender);
        e.Handled = true;
        if (ServiceFrom(sender) is not { CanChangeFrontendPort: true } service ||
            SelectedProject is not { } project || !CanEdit || IsEditing ||
            _closing || _closed || _closeRequested || !Services.Contains(service)) return;

        var originalProfile = JsonSerializer.Serialize(service.Profile);
        var directory = service.Directory;
        var stopRevision = service.Runner.StopRevision;
        // A modal dispatcher still receives bridge requests. Block service actions
        // for the whole draft so Save can inspect and persist without a restart race.
        _servicePortEditing = true;
        SetSavingProjectEdits(true);
        try
        {
            var editor = new ServicePortWindow(service.Name,
                ServicePortConfiguration.GetPort(service.Profile),
                ServicePortConfiguration.GetEditUnavailableReason(service.Profile, directory),
                service.IsStoppedForPortEdit,
                port => SaveFrontendPortAsync(project, service, originalProfile, directory, stopRevision, port))
            { Owner = this };
            editor.ShowDialog();
        }
        catch (Exception)
        {
            Notice = "The frontend port editor could not be opened. Reopen the launcher and retry.";
        }
        finally
        {
            _servicePortEditing = false;
            if (_savingProjectEdits) SetSavingProjectEdits(false);
        }
    }

    private async Task SaveFrontendPortAsync(ProjectProfile project, ServiceViewModel service,
        string originalProfile, string directory, int stopRevision, string portText)
    {
        RequireCurrent();
        if (service.IsBusy || service.IsStopping || _batchBusy || _forceStopBatchBusy)
            throw new InvalidOperationException("Wait for the current service operation before saving. Your port draft is retained.");

        await service.Runner.RefreshForProfileEditAsync();
        RequireCurrent();
        service.Update();
        if (!service.IsStoppedForPortEdit)
            throw new InvalidOperationException($"Stop {service.Name} and complete a successful process check before changing its port. Close this editor to stop the service; your settings have not been saved.");

        // Clone only at save time so unrelated preferences and notes remain current.
        // This frontend-only edit does not change any Angular API proxy target.
        var candidateSettings = JsonSerializer.Deserialize<LauncherSettings>(JsonSerializer.Serialize(_settings))!;
        var saved = candidateSettings.Projects.Single(item => item.Id == project.Id).Services
            .Single(item => item.Id == service.Profile.Id);
        ServicePortConfiguration.Apply(saved, directory, portText);
        var port = ServicePortConfiguration.GetPort(saved);
        var conflict = candidateSettings.Projects.SelectMany(item => item.Services)
            .FirstOrDefault(other => other.Id != saved.Id && ServicePortConfiguration.GetPort(other) == port);
        if (conflict is not null)
            throw new InvalidOperationException($"Port {port} is already assigned to {conflict.Name}. Choose a different port for {service.Name}.");

        RequireCurrent();
        _store.Save(candidateSettings);
        // Keep original runner/profile identities, captured output and service links.
        service.Profile.Url = saved.Url;
        service.Profile.StartCommand = saved.StartCommand;
        service.Update();
        UpdateActions();
        Notice = $"Saved {service.Name} port {port}. Applies on the next start.";

        void RequireCurrent()
        {
            if (_closing || _closed || _closeRequested || IsEditing || !_servicePortEditing || !_savingProjectEdits ||
                !ReferenceEquals(project, SelectedProject) || !_settings.Projects.Contains(project) ||
                !project.Services.Contains(service.Profile) || !Services.Contains(service) ||
                !_runners.TryGetValue(project.Id, out var known) || !known.Contains(service) ||
                !service.IsFrontendService || service.Runner.StopRevision != stopRevision ||
                !service.Directory.Equals(directory, StringComparison.OrdinalIgnoreCase) ||
                JsonSerializer.Serialize(service.Profile) != originalProfile)
                throw new InvalidOperationException("The frontend or project changed while its port editor was open. Reopen the editor before saving.");
        }
    }
}
