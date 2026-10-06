using System.IO;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class MainWindow
{
    private async Task RequireStoppedProxyFileServicesAsync(AngularDevProxyConfiguration proxyChanges, ProjectProfile candidate)
    {
        if (proxyChanges.UpdatedFileCount == 0) return;
        if (!_savingProjectEdits)
            throw new InvalidOperationException("Block service actions before checking shared Angular development proxy files.");

        var fileDirectories = proxyChanges.ChangedFilePaths.Select(path => Path.GetDirectoryName(path)!).ToArray();
        // A nested app can use its parent workspace's proxy, even when the proxy itself
        // is in a sibling configuration folder. Preserve the full frontend folder scope.
        var directories = candidate.Services.Select(service => _store.ResolveWorkingDirectory(candidate, service))
            .Where(directory => fileDirectories.Any(fileDirectory =>
                SettingsStore.WorkingFoldersOverlap(directory, fileDirectory)))
            .Concat(fileDirectories)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // Archived and unselected profiles can own processes using the same source files.
        // Check actual staged file changes; merely sharing a folder does not block a save.
        var affected = _settings.Projects.SelectMany(project =>
            (_runners.GetValueOrDefault(project.Id) ?? []).Where(service => directories.Any(directory =>
                    SettingsStore.WorkingFoldersOverlap(directory, service.Directory)))
                .Select(service => (Project: project, Service: service))).ToArray();
        foreach (var item in affected)
            if (item.Service.IsBusy || item.Service.IsStopping) RequireStopped(item.Project.Name, item.Service.Name);

        await Task.WhenAll(affected.Select(item => item.Service.Runner.RefreshForProfileEditAsync()));
        foreach (var item in affected)
        {
            var service = item.Service;
            service.Update();
            if (service.IsBusy || service.IsStopping || service.Runner.HasManagedProcess ||
                !service.Runner.HasVerifiedNoServiceProcesses || service.Runner.Snapshot.ProcessIds.Count != 0)
                RequireStopped(item.Project.Name, service.Name);
        }

        static void RequireStopped(string projectName, string serviceName) =>
            throw new InvalidOperationException($"Stop {projectName} / {serviceName} and complete a fresh process check " +
                "before changing its shared Angular development proxy. Your settings have not been saved.");
    }
}
