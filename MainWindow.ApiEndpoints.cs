using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using FullStackLauncher.Models;
using FullStackLauncher.Services;
using FullStackLauncher.ViewModels;
using Microsoft.Win32;

namespace FullStackLauncher;

public partial class MainWindow
{
    private readonly Dictionary<ServiceViewModel, ApiEndpointsWindow> _apiEndpointWindows = [];
    private readonly Dictionary<ServiceViewModel, ApiEndpointCountContext> _apiEndpointCountContexts = [];
    private readonly Dictionary<ServiceViewModel, CancellationTokenSource> _apiEndpointCountRuns = [];

    private sealed record ApiEndpointCountContext(ProjectProfile Project, string Directory, string? SourceDirectory = null);

    private async void CountApiEndpoints_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (ServiceFrom(sender) is { } service) await CountApiEndpointsAsync(service);
    }

    private async void ChooseApiEndpointFolder_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_closing || _closed || _closeRequested || IsEditing ||
            ServiceFrom(sender) is not { ShowApiDatabaseLabel: true } service || !service.ApiEndpoints.CanChooseSourceFolder ||
            SelectedProject is not { } project) return;
        var directory = service.Directory;
        var popup = (sender as FrameworkElement)?.Tag as Popup;
        var restorePopup = true;
        popup?.SetCurrentValue(Popup.IsOpenProperty, false);
        try
        {
            var picker = new OpenFolderDialog
            {
                Title = "Choose this API's controllers or endpoints folder",
                InitialDirectory = _apiEndpointCountContexts.GetValueOrDefault(service)?.SourceDirectory ?? directory,
                Multiselect = false
            };
            if (picker.ShowDialog(this) == true && IsCurrentApiEndpointFolderChoice(project, service, directory))
            {
                var count = CountApiEndpointsAsync(service, picker.FolderName);
                // BeginCount runs before the popup reopens, retaining visible progress without
                // its Opened handler starting a second automatic scan.
                popup?.SetCurrentValue(Popup.IsOpenProperty, true);
                restorePopup = false;
                await count;
            }
        }
        catch (Exception)
        {
            if (IsCurrentApiEndpointFolderChoice(project, service, directory))
                service.ApiEndpoints.Fail("Source folder could not open. Retry.");
        }
        finally
        {
            if (restorePopup && IsCurrentApiEndpointFolderChoice(project, service, directory))
                popup?.SetCurrentValue(Popup.IsOpenProperty, true);
        }
    }

    private bool IsCurrentApiEndpointFolderChoice(ProjectProfile project, ServiceViewModel service, string directory) =>
        !_closing && !_closed && !_closeRequested && ReferenceEquals(SelectedProject, project) && Projects.Contains(project) &&
        _runners.GetValueOrDefault(project.Id)?.Contains(service) == true && service.ShowApiDatabaseLabel &&
        string.Equals(service.Directory, directory, StringComparison.OrdinalIgnoreCase);

    private async Task CountApiEndpointsAsync(ServiceViewModel service, string? sourceDirectory = null)
    {
        if (_closing || _closed || _closeRequested || !service.ShowApiDatabaseLabel || service.ApiEndpoints.IsBusy) return;
        var project = Projects.FirstOrDefault(candidate =>
            _runners.GetValueOrDefault(candidate.Id)?.Contains(service) == true);
        if (project is null || !ReferenceEquals(SelectedProject, project)) return;

        var previous = _apiEndpointCountContexts.GetValueOrDefault(service);
        var context = new ApiEndpointCountContext(project, service.Directory, sourceDirectory ??
            (previous is not null && ReferenceEquals(previous.Project, project) &&
             string.Equals(previous.Directory, service.Directory, StringComparison.OrdinalIgnoreCase) ? previous.SourceDirectory : null));
        _apiEndpointCountContexts[service] = context;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_sourceLineCountLifetime.Token);
        cancellation.CancelAfter(TimeSpan.FromMinutes(5));
        _apiEndpointCountRuns[service] = cancellation;
        if (!service.ApiEndpoints.BeginCount()) return;
        try
        {
            // Paint the progress bar before starting source discovery on its worker.
            await Dispatcher.Yield(DispatcherPriority.Render);
            var branch = await ReadBranchFolderAsync(context.Directory).WaitAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!IsCurrentApiEndpointCount(service, context)) return;
            if (!HasApiEndpointBranch(service, branch)) return;
            if (!await HasApiEndpointSourceBranchAsync(service, context, branch, cancellation.Token)) return;

            var result = context.SourceDirectory is { } folder
                ? await ApiEndpointDiscovery.AnalyzeSourceFolderAsync(folder, cancellation.Token)
                : await ApiEndpointDiscovery.AnalyzeSourceAsync(context.Directory, cancellation.Token);
            var finalBranch = await ReadBranchFolderAsync(context.Directory).WaitAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!IsCurrentApiEndpointCount(service, context)) return;
            if (!HasApiEndpointBranch(service, finalBranch)) return;
            if (!string.Equals(branch.RepositoryPath, finalBranch.RepositoryPath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(branch.DisplayText, finalBranch.DisplayText, StringComparison.Ordinal))
            {
                service.ApiEndpoints.Fail("Branch changed. Recount.");
                return;
            }
            if (!await HasApiEndpointSourceBranchAsync(service, context, finalBranch, cancellation.Token)) return;
            cancellation.Token.ThrowIfCancellationRequested();
            if (!IsCurrentApiEndpointCount(service, context)) return;
            service.ApiEndpoints.Complete(result, branch.DisplayText);
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentApiEndpointCount(service, context))
                service.ApiEndpoints.Fail("Endpoint count timed out. Retry.");
        }
        catch (ApiEndpointDiscoveryException ex)
        {
            if (IsCurrentApiEndpointCount(service, context)) service.ApiEndpoints.Fail(ex.Message);
        }
        catch (Exception)
        {
            if (IsCurrentApiEndpointCount(service, context))
                service.ApiEndpoints.Fail("Endpoints unavailable. Check folder access and retry.");
        }
        finally
        {
            if (_apiEndpointCountRuns.TryGetValue(service, out var current) && ReferenceEquals(current, cancellation))
            {
                _apiEndpointCountRuns.Remove(service);
                if (service.ApiEndpoints.IsBusy) service.ApiEndpoints.Cancel();
            }
        }
    }

    private async Task<bool> HasApiEndpointSourceBranchAsync(ServiceViewModel service, ApiEndpointCountContext context,
        GitBranchSnapshot branch, CancellationToken token)
    {
        if (context.SourceDirectory is not { } folder) return true;
        var sourceBranch = await ReadBranchFolderAsync(folder).WaitAsync(token);
        token.ThrowIfCancellationRequested();
        if (!IsCurrentApiEndpointCount(service, context)) return false;
        if (sourceBranch.State == GitBranchState.Branch &&
            string.Equals(branch.RepositoryPath, sourceBranch.RepositoryPath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(branch.DisplayText, sourceBranch.DisplayText, StringComparison.Ordinal)) return true;
        service.ApiEndpoints.Fail(sourceBranch.State switch
        {
            GitBranchState.MissingDirectory => "Source folder unavailable. Choose another folder.",
            GitBranchState.Unavailable => "Source branch unavailable. Retry or choose another folder.",
            _ => "Choose a source folder in this API's selected Git repository."
        });
        return false;
    }

    private static bool HasApiEndpointBranch(ServiceViewModel service, GitBranchSnapshot branch)
    {
        if (branch.State == GitBranchState.Branch) return true;
        service.ApiEndpoints.Fail(branch.State switch
        {
            GitBranchState.Detached or GitBranchState.NotRepository => "Select a branch.",
            GitBranchState.MissingDirectory => "API folder unavailable.",
            _ => "Branch unavailable. Retry."
        }, needsBranch: branch.State is GitBranchState.Detached or GitBranchState.NotRepository);
        return false;
    }

    private bool IsCurrentApiEndpointCount(ServiceViewModel service, ApiEndpointCountContext context) =>
        !_closed && !_closing && !_closeRequested && !_sourceLineCountLifetime.IsCancellationRequested &&
        ReferenceEquals(SelectedProject, context.Project) && Projects.Contains(context.Project) &&
        _runners.GetValueOrDefault(context.Project.Id)?.Contains(service) == true && service.ShowApiDatabaseLabel &&
        string.Equals(service.Directory, context.Directory, StringComparison.OrdinalIgnoreCase) &&
        _apiEndpointCountContexts.TryGetValue(service, out var current) && ReferenceEquals(current, context);

    private void ApiEndpoints_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _closeRequested || ServiceFrom(sender) is not { ShowApiDatabaseLabel: true } service) return;
        if (_apiEndpointWindows.TryGetValue(service, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        var project = Projects.FirstOrDefault(candidate =>
            _runners.GetValueOrDefault(candidate.Id)?.Contains(service) == true);
        if (project is null) return;
        var sourceDirectory = _apiEndpointCountContexts.GetValueOrDefault(service)?.SourceDirectory;
        var window = new ApiEndpointsWindow(project.Name, service.Name, service.Directory, sourceDirectory) { Owner = this };
        // Statistics owns its branch-validated source result. Independent detail scans/imports
        // must not replace it or bypass its progress and branch-selection state.
        window.Closed += (_, _) => _apiEndpointWindows.Remove(service);
        _apiEndpointWindows.Add(service, window);
        window.Show();
    }

    private void RefreshApiEndpointWindows()
    {
        foreach (var (service, context) in _apiEndpointCountContexts.ToArray())
        {
            var sameService = Projects.Contains(context.Project) &&
                _runners.GetValueOrDefault(context.Project.Id)?.Contains(service) == true && service.ShowApiDatabaseLabel &&
                string.Equals(service.Directory, context.Directory, StringComparison.OrdinalIgnoreCase);
            if (sameService && (!_apiEndpointCountRuns.ContainsKey(service) || IsCurrentApiEndpointCount(service, context))) continue;
            if (_apiEndpointCountRuns.TryGetValue(service, out var cancellation)) cancellation.Cancel();
            _apiEndpointCountContexts.Remove(service);
            service.ApiEndpoints.Reset();
        }
        foreach (var (service, window) in _apiEndpointWindows.ToArray())
        {
            var project = Projects.FirstOrDefault(candidate =>
                _runners.GetValueOrDefault(candidate.Id)?.Contains(service) == true);
            if (project is null || !service.ShowApiDatabaseLabel ||
                !string.Equals(service.Directory, window.WorkingDirectory, StringComparison.OrdinalIgnoreCase))
            {
                service.ApiEndpoints.SetInventory(null);
                window.Close();
            }
            else window.UpdateContext(project.Name, service.Name);
        }
    }
}
