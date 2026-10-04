using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class MainWindow
{
    private bool _isEditing;
    private bool _savingProjectEdits;
    private string _draftProjectName = "";
    private string _editError = "";
    public bool IsEditing => _isEditing;
    public bool CanChangeProject => !IsEditing && !_addingProjectService && !_savingProjectEdits && !_closeRequested && !_closing && !_forceStopBatchBusy;
    public bool CanRemoveProject => !IsEditing && CanEdit;
    public bool CanSaveProjectEdits => IsEditing && CanEdit;
    public bool CanCancelProjectEdits => IsEditing && !_savingProjectEdits && !_closing;
    public bool CanEditDetails => CanEdit && Services.All(service => !service.Runner.HasManagedProcess && service.Runner.Snapshot.ProcessIds.Count == 0);
    public string DraftProjectName { get => _draftProjectName; set { _draftProjectName = value; Changed(nameof(DraftProjectName)); } }
    public string EditError { get => _editError; private set { _editError = value; Changed(nameof(EditError)); Changed(nameof(HasEditError)); } }
    public bool HasEditError => EditError.Length > 0;
    public string EditSaveLabel => _savingProjectEdits ? "Saving…" : "Save changes";

    private void BeginProjectEditing()
    {
        if (!CanEdit || IsEditing || SelectedProject is null) return;
        DraftProjectName = SelectedProject.Name;
        foreach (var service in Services) service.BeginEditing();
        EditError = "";
        SetProjectEditMode(true);
        // Reveal the fields even when the service section was previously hidden.
        ServicesVisible = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { ProjectNameEditor.Focus(); ProjectNameEditor.SelectAll(); });
    }

    private void SetProjectEditMode(bool editing)
    {
        _isEditing = editing;
        if (!editing) foreach (var service in Services) service.EndEditing();
        Changed(nameof(IsEditing));
        Changed(nameof(CanRemoveProject));
        ApplyLayout();
        UpdateActions();
    }

    private void SetSavingProjectEdits(bool saving)
    {
        _savingProjectEdits = saving;
        foreach (var service in Services) service.IsSavingEdits = saving;
        Changed(nameof(EditSaveLabel));
        UpdateActions();
    }

    private void ProjectTitle_Click(object sender, RoutedEventArgs e) => BeginProjectEditing();

    private async void EditProject_Click(object sender, RoutedEventArgs e)
    {
        // The toggle stays checked on validation/save failure, including while awaiting a fresh status check.
        Changed(nameof(IsEditing));
        if (IsEditing) await SaveProjectEditsAsync();
        else BeginProjectEditing();
        Changed(nameof(IsEditing));
    }

    private async void SaveProjectEdits_Click(object sender, RoutedEventArgs e) => await SaveProjectEditsAsync();

    private void CancelProjectEdits_Click(object sender, RoutedEventArgs e)
    {
        if (_savingProjectEdits) return;
        CancelProjectEdits();
    }

    private void CancelProjectEdits()
    {
        EditError = "";
        SetProjectEditMode(false);
        Notice = "Project edits canceled.";
        EditModeToggle.Focus();
    }

    private bool HasProjectEdits => SelectedProject is { } project &&
        (DraftProjectName.Trim() != project.Name || Services.Any(service => service.DraftName.Trim() != service.Name || PortWasEdited(service)));

    private static bool PortWasEdited(ViewModels.ServiceViewModel service) =>
        service.DraftPort.Trim() != (ServicePortConfiguration.GetPort(service.Profile)?.ToString() ?? "");

    private ViewModels.ServiceViewModel[] DependentAngularFrontends(ViewModels.ServiceViewModel api)
    {
        var profiles = Services.Select(service => service.Profile).ToArray();
        var dependentIds = ApiPortEditPolicy.DependentAngularFrontends(api.Profile, profiles)
            .Select(profile => profile.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Services.Where(service => dependentIds.Contains(service.Profile.Id)).ToArray();
    }

    private void RefreshLinkedPortAvailability()
    {
        var services = Services.ToArray();
        var profiles = services.Select(service => service.Profile).ToArray();
        var byId = services.ToDictionary(service => service.Profile.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var api in Services)
        {
            var frontends = DependentAngularFrontends(api);
            var explicitlyLinked = frontends.Any(frontend =>
                string.Equals(frontend.Profile.ApiTargetServiceId, api.Profile.Id, StringComparison.OrdinalIgnoreCase));
            var blockingProfile = ApiPortEditPolicy.FirstBlockingService(api.Profile, profiles,
                profile => byId[profile.Id].IsStoppedForPortEdit);
            var frontendBlocker = blockingProfile is not null &&
                !blockingProfile.Id.Equals(api.Profile.Id, StringComparison.OrdinalIgnoreCase)
                    ? blockingProfile : null;
            var frontendNames = string.Join(", ", frontends.Select(frontend => frontend.Name));
            api.SetPortEditDependencyStatus(frontendBlocker is null ? null
                    : $"Stop Angular frontend {frontendBlocker.Name} and wait for a successful process check before changing this API port.",
                frontends.Length == 0 ? null : explicitlyLinked
                    ? $"Linked to {frontendNames}. Saving a new API port updates the development proxy. Start the API and linked frontends afterward."
                    : $"Automatic port matching may update development proxies for {frontendNames}. Start affected services afterward.");
        }
    }

    private async Task<bool> SaveProjectEditsAsync()
    {
        if (!CanSaveProjectEdits || SelectedProject is not { } selected) return false;
        EditError = "";
        if (string.IsNullOrWhiteSpace(DraftProjectName))
        {
            EditError = "Enter a project name.";
            ProjectNameEditor.Focus();
            return false;
        }
        var services = Services.ToArray();
        foreach (var service in services)
        {
            if (string.IsNullOrWhiteSpace(service.DraftName))
            {
                EditError = $"Enter a service name for {service.Name}.";
                return false;
            }
        }
        if (!HasProjectEdits)
        {
            SetProjectEditMode(false);
            EditModeToggle.Focus();
            return true;
        }

        SetSavingProjectEdits(true);
        try
        {
            var portChanges = services.Where(PortWasEdited).ToArray();
            var dependentFrontends = portChanges.SelectMany(DependentAngularFrontends)
                .Distinct().ToArray();
            // Every frontend that may receive a development proxy update must be stopped.
            var inspected = portChanges.Concat(dependentFrontends).Distinct().ToArray();
            await Task.WhenAll(inspected.Select(service => service.Runner.RefreshForProfileEditAsync()));
            if (!ReferenceEquals(selected, SelectedProject) || !services.SequenceEqual(Services))
                throw new InvalidOperationException("The selected project changed. Reopen editing before saving.");
            foreach (var service in inspected) service.Update();
            var profiles = services.Select(service => service.Profile).ToArray();
            var byId = services.ToDictionary(service => service.Profile.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var service in portChanges)
            {
                var blocker = ApiPortEditPolicy.FirstBlockingService(service.Profile, profiles,
                    profile => byId[profile.Id].IsStoppedForPortEdit);
                if (blocker is null) continue;
                var blockerName = byId[blocker.Id].Name;
                if (blocker.Id.Equals(service.Profile.Id, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Stop {blockerName} before changing its port. Your edits are still here.");
                throw new InvalidOperationException($"Stop Angular frontend {blockerName} before changing its API port. Your edits are still here.");
            }

            // Clone at save time to preserve unrelated selections/preferences saved while editing.
            var candidateSettings = JsonSerializer.Deserialize<LauncherSettings>(JsonSerializer.Serialize(_settings))!;
            var candidate = candidateSettings.Projects.Single(project => project.Id == selected.Id);
            candidate.Name = DraftProjectName.Trim();
            foreach (var service in services)
            {
                var profile = candidate.Services.Single(profile => profile.Id == service.Profile.Id);
                profile.Name = service.DraftName.Trim();
                if (!PortWasEdited(service)) continue;
                try { ServicePortConfiguration.Apply(profile, service.Directory, service.DraftPort); }
                catch (InvalidOperationException ex) { throw new InvalidOperationException($"{profile.Name}: {ex.Message}", ex); }
            }
            foreach (var service in portChanges)
            {
                var profile = candidate.Services.Single(profile => profile.Id == service.Profile.Id);
                var port = ServicePortConfiguration.GetPort(profile);
                var conflict = candidateSettings.Projects.SelectMany(project => project.Services)
                    .FirstOrDefault(other => other.Id != profile.Id && ServicePortConfiguration.GetPort(other) == port);
                if (conflict is not null)
                    throw new InvalidOperationException($"Port {port} is already assigned to {conflict.Name}. Choose a different port for {profile.Name}.");
            }
            var proxyChanges = AngularDevProxyConfiguration.Prepare(selected, candidate, _store);
            proxyChanges.SaveWithSettings(() => _store.Save(candidateSettings));

            // Preserve original profiles and runners: they own live processes, logs, notes and database state.
            selected.Name = candidate.Name;
            foreach (var service in services)
            {
                var saved = candidate.Services.Single(profile => profile.Id == service.Profile.Id);
                service.Profile.Name = saved.Name;
                service.Profile.Url = saved.Url;
                service.Profile.StartCommand = saved.StartCommand;
                service.Update();
            }
            ProjectItems.First(item => ReferenceEquals(item.Profile, selected)).RefreshName();
            Changed(nameof(SelectedProject));
            _projectTasksWindow?.ShowProject(selected, _store.ResolveRoot(selected));
            SetProjectEditMode(false);
            Notice = portChanges.Length > 0
                ? $"Saved {selected.Name}. Updated ports will be used on the next start."
                : $"Saved {selected.Name}.";
            if (proxyChanges.UpdatedFileCount > 0)
                Notice += " Angular's local API proxy was updated. Start the linked frontend to load the new API port.";
            EditModeToggle.Focus();
            return true;
        }
        catch (Exception ex)
        {
            EditError = $"Changes were not saved. {ex.Message}";
            Notice = "Project edits are still open. Correct the highlighted fields or retry saving.";
            return false;
        }
        finally { SetSavingProjectEdits(false); }
    }

    private async void ProjectEditField_KeyDown(object sender, KeyEventArgs e)
    {
        if (!IsEditing || _savingProjectEdits) return;
        if (e.Key == Key.Enter) { e.Handled = true; await SaveProjectEditsAsync(); }
        else if (e.Key == Key.Escape) { e.Handled = true; CancelProjectEdits(); }
    }

    private async void ProjectEditWindow_KeyDown(object sender, KeyEventArgs e)
    {
        if (!IsEditing || _savingProjectEdits) return;
        if (e.Key == Key.Escape) { e.Handled = true; CancelProjectEdits(); }
        else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        { e.Handled = true; await SaveProjectEditsAsync(); }
    }

    private async void ProjectDetails_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditDetails || !await SaveProjectEditsAsync()) return;
        OpenProjectDetails();
    }

    private async Task<bool> ResolveProjectEditsForCloseAsync()
    {
        if (!IsEditing || !HasProjectEdits) return true;
        var result = MessageBox.Show(this, "Save your project name, service names and port changes before closing?",
            "Unsaved project edits", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (result == MessageBoxResult.Cancel) return false;
        if (result == MessageBoxResult.Yes) return await SaveProjectEditsAsync();
        CancelProjectEdits();
        return true;
    }
}
