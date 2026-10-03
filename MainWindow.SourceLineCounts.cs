using System.Windows;
using FullStackLauncher.Models;
using FullStackLauncher.Services;
using FullStackLauncher.ViewModels;

namespace FullStackLauncher;

public partial class MainWindow
{
    private readonly CancellationTokenSource _sourceLineCountLifetime = new();

    private async Task LoadServiceLineCountsAsync(ProjectProfile project, ServiceViewModel[] services)
    {
        // Read only saved counts on selection. Source scans require the service's button.
        var settingsPath = _store.SettingsPath;
        await Task.WhenAll(services.Select(async service =>
        {
            if (!service.SourceLines.BeginLoad()) return;
            try
            {
                var snapshot = await Task.Run(() => new SourceLineCountStore(settingsPath).Load(project.Id, service.Profile.Id, service.Directory),
                    _sourceLineCountLifetime.Token);
                if (IsCurrentLineCountService(project, service)) service.SourceLines.Complete(snapshot);
            }
            catch (OperationCanceledException) when (_sourceLineCountLifetime.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (IsCurrentLineCountService(project, service))
                    service.SourceLines.Fail(HistoryFailure(ex,
                        "Saved line count could not be read. Check access to the local count history and retry. Existing history has been preserved."));
            }
        }));
    }

    private async void CountServiceLines_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _closed || _closeRequested || ServiceFrom(sender) is not { } service ||
            !service.SourceLines.CanCount || SelectedProject is not { } project) return;
        if (!IsCurrentLineCountService(project, service)) return;

        var directory = service.Directory;
        var serviceId = service.Profile.Id;
        var settingsPath = _store.SettingsPath;
        var saving = false;
        service.SourceLines.BeginCount();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_sourceLineCountLifetime.Token);
        cancellation.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            var count = await Task.Run(() => SourceLineCounter.Count(directory, cancellation.Token), cancellation.Token);
            // A project edit/removal can replace the card while its old folder is being read.
            if (!IsCurrentLineCountService(project, service)) return;
            saving = true;
            var snapshot = await Task.Run(() =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                return new SourceLineCountStore(settingsPath).Save(project.Id, serviceId, directory, count);
            }, cancellation.Token);
            if (IsCurrentLineCountService(project, service)) service.SourceLines.Complete(snapshot);
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentLineCountService(project, service))
                service.SourceLines.Fail("Counting timed out. The previous saved count has been kept. Retry when the folder is available.");
        }
        catch (Exception ex)
        {
            if (IsCurrentLineCountService(project, service))
                service.SourceLines.Fail(saving
                    ? HistoryFailure(ex, "The count could not be saved. The previous saved count has been kept. Check access to local count history and retry.")
                    : ex is System.IO.IOException
                        ? $"{ex.Message}\nThe previous saved count has been kept."
                        : "The source folder could not be counted completely. The previous count has been kept. Check folder access and retry.");
        }
        finally
        {
            if (!service.SourceLines.CanCount) service.SourceLines.Cancel();
        }
    }

    private bool IsCurrentLineCountService(ProjectProfile project, ServiceViewModel service) =>
        !_closed && !_sourceLineCountLifetime.IsCancellationRequested && Projects.Contains(project) &&
        _runners.TryGetValue(project.Id, out var current) && current.Contains(service);

    private static string HistoryFailure(Exception error, string fallback) =>
        error is System.IO.InvalidDataException or InvalidOperationException ? error.Message : fallback;
}
