using System.Windows;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class MainWindow
{
    private async void CloneProjectRepositories_Click(object sender, RoutedEventArgs e)
    {
        if (!CanAddService || SelectedProject is not { } selected) return;
        IReadOnlyList<string> repositories;
        try
        {
            _addingProjectService = true;
            UpdateActions();
            var source = new NewProjectWindow(_store.BaseDirectory, _settings.Projects,
                existingWorkspaceFolder: _store.ResolveRoot(selected)) { Owner = this };
            if (source.ShowDialog() != true) return;
            repositories = source.ImportedRepositories;
        }
        catch (Exception ex)
        {
            Notice = $"Repository import could not finish: {SensitiveDataProtection.Redact(ex.Message)}";
            MessageBox.Show(this, Notice, "Clone repositories", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        finally
        {
            _addingProjectService = false;
            UpdateActions();
        }

        if (!ReferenceEquals(selected, SelectedProject) || repositories.Count == 0) return;
        var savedServiceCount = selected.Services.Count;
        while (CanAddService && ReferenceEquals(selected, SelectedProject))
        {
            string? folder;
            try
            {
                _addingProjectService = true;
                UpdateActions();
                var services = new RepositoryServicesWindow(selected.Name, repositories) { Owner = this };
                if (services.ShowDialog() != true) break;
                folder = services.SelectedFolder;
            }
            finally
            {
                _addingProjectService = false;
                UpdateActions();
            }
            if (folder is null) break;
            await AddProjectServiceAsync(folder);
        }

        // Refresh only this project's discovery after clones become visible. Adding a
        // service uses the existing detached save and leaves every old runner intact.
        if (SelectedProjectItem is { } item && ReferenceEquals(item.Profile, selected))
        {
            try
            {
                await _projectGitService.RefreshProjectAsync(selected.Id, ProjectGitFolders(item),
                    fetchRemote: false, token: _dashboardGitLifetime.Token);
                await PresentSharedProjectGitAsync();
                var addedCount = selected.Services.Count - savedServiceCount;
                Notice = $"Cloned {repositories.Count} repository(s) into {selected.Name}."
                    + (addedCount > 0 ? $" Added {addedCount} service(s)." : " Add services later with Add Service.");
            }
            catch (OperationCanceledException) when (_closeRequested || _closing || _closed) { }
            catch (Exception ex)
            {
                Notice = $"Repositories cloned, but Git refresh could not finish: {SensitiveDataProtection.Redact(ex.Message)}";
            }
        }
    }
}
