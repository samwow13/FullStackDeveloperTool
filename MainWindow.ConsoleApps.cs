using System.IO;
using System.Text.Json;
using System.Windows;
using FullStackLauncher.Models;
using FullStackLauncher.Services;
using Microsoft.Win32;

namespace FullStackLauncher;

public partial class MainWindow
{
    private bool _addingProjectService;
    public bool CanAddService => CanEdit && !IsEditing && !_checkingStartupServices;

    private async void AddService_Click(object sender, RoutedEventArgs e) =>
        await AddProjectServiceAsync();

    private async Task AddProjectServiceAsync()
    {
        if (!CanAddService || SelectedProject is not { } selected) return;
        var serviceType = "Service";
        _addingProjectService = true;
        UpdateActions();
        ServiceProfile? savedService = null;
        try
        {
            var picker = new OpenFolderDialog
            {
                Title = "Choose the service folder", Multiselect = false
            };
            var projectRoot = _store.ResolveRoot(selected);
            if (Directory.Exists(projectRoot)) picker.InitialDirectory = projectRoot;
            if (picker.ShowDialog(this) != true) return;

            var typePicker = new ServiceTypeWindow(picker.FolderName) { Owner = this };
            if (typePicker.ShowDialog() != true || typePicker.IsApi is not { } isApi) return;
            serviceType = isApi ? "API" : "Console app";
            var startAction = isApi ? "Start" : "Run";

            Notice = "Detecting app type…";
            ServiceCommandDetection detection;
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                try
                {
                    detection = await ServiceCommandDiscovery.DetectAsync(picker.FolderName, isApi, cancellation.Token)
                        .WaitAsync(cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    detection = new([], ["Detection timed out. Choose the type and review the command."]);
                }
            }

            void SaveService(ServiceProfile service)
            {
                if (_closeRequested || _closing || !ReferenceEquals(selected, SelectedProject) ||
                    !_settings.Projects.Contains(selected))
                    throw new InvalidOperationException("The selected project changed. Reopen the add dialog and retry.");

                // Save a detached copy of the latest settings. Existing runners and
                // profiles remain attached to their running processes and output.
                var candidateSettings = JsonSerializer.Deserialize<LauncherSettings>(JsonSerializer.Serialize(_settings))!;
                var candidate = candidateSettings.Projects.Single(project => project.Id == selected.Id);
                candidate.Services.Add(service);
                _store.Save(candidateSettings);
                selected.Services.Add(service);
                savedService = service;
            }

            Window editor = isApi
                ? new ApiServiceWindow(selected, _store.BaseDirectory, _settings.Projects, SaveService,
                    detection: detection, workingDirectory: picker.FolderName)
                : new ConsoleServiceWindow(selected, _store.BaseDirectory, _settings.Projects, SaveService,
                    detection: detection, workingDirectory: picker.FolderName);
            editor.Owner = this;
            if (editor.ShowDialog() != true || savedService is null)
            {
                Notice = "App setup canceled.";
                return;
            }

            var runners = GetRunners(selected);
            var added = CreateServiceViewModel(selected, savedService);
            runners.Add(added);
            Services.Add(added);
            ProjectItems.First(item => ReferenceEquals(item.Profile, selected)).UpdateServices(runners);
            foreach (var service in runners) service.Update();
            _ = LoadServiceLineCountsAsync(selected, [added]);
            Notice = $"Added {savedService.Name}. Choose {startAction} to start it.";
        }
        catch (Exception ex)
        {
            if (savedService is null) ShowSaveError(ex);
            else
            {
                Notice = $"{serviceType} was saved, but the dashboard could not refresh: {ex.Message}. Reopen the launcher to load it.";
                MessageBox.Show(this, Notice, "Refresh failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            _addingProjectService = false;
            UpdateActions();
        }
        if (savedService is not null)
            await Task.WhenAll(RefreshAsync(fresh: true), RefreshProjectBranchesAsync());
    }
}
