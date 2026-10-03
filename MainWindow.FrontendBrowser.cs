using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using FullStackLauncher.Models;
using FullStackLauncher.ProjectTasks;
using FullStackLauncher.Services;
using FullStackLauncher.ViewModels;

namespace FullStackLauncher;

public partial class MainWindow
{
    private readonly CancellationTokenSource _frontendBrowserLifetime = new();
    private readonly Dictionary<ServiceRunner, CancellationTokenSource> _frontendBrowserRequests = [];

    private void InitializeFrontendBrowserAccess()
    {
        if (_settings.Projects.SelectMany(project => project.Services)
            .Any(profile => profile.OpenAfterBuild && FrontendServiceSupport.IsFrontend(profile)))
            PrepareFrontendBrowserAccess();
    }

    private static void PrepareFrontendBrowserAccess() => _ = Task.Run(() =>
    {
        try { BrowserPageCaptureBroker.PrepareSiteAccess(); }
        catch { /* The actual opening request reports unavailable tab access. */ }
    });

    private void OpenAfterBuild_Click(object sender, RoutedEventArgs e)
    {
        if (ServiceFrom(sender) is not { } service) return;
        if (!service.CanChangeOpenAfterBuild ||
            _closing || _closed || _closeRequested || _savingProjectEdits || IsEditing || SelectedProject is not { } project)
        {
            service.RefreshOpenAfterBuild();
            return;
        }
        var enabled = !service.OpenAfterBuild;
        try
        {
            var candidate = JsonSerializer.Deserialize<LauncherSettings>(JsonSerializer.Serialize(_settings))!;
            candidate.Projects.Single(item => item.Id == project.Id).Services
                .Single(item => item.Id == service.Profile.Id).OpenAfterBuild = enabled;
            _store.Save(candidate);
            service.Profile.OpenAfterBuild = enabled;
            if (!enabled && _frontendBrowserRequests.TryGetValue(service.Runner, out var request)) request.Cancel();
            if (enabled) PrepareFrontendBrowserAccess();
            service.SetBrowserOpenStatus(enabled
                ? "Enabled for the next managed start or restart."
                : "Automatic opening is off.");
            Notice = $"{service.Name}: Open after build is {(enabled ? "on" : "off")}.";
        }
        catch
        {
            Notice = "Open after build could not be saved. The previous saved choice is kept.";
        }
        finally { service.Update(); service.RefreshOpenAfterBuild(); }
    }

    private void QueueFrontendBrowserOpen(ProjectProfile project, ServiceViewModel service, string url, long generation)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        _ = Dispatcher.BeginInvoke(new Action(() => _ = OpenReadyFrontendAsync(project, service, url, generation)),
            DispatcherPriority.Background);
    }

    private bool CanOpenFrontend(ProjectProfile project, ServiceViewModel service, long generation) =>
        !_closing && !_closed && !_closeRequested && !_savingProjectEdits && !IsEditing &&
        service.OpenAfterBuild && service.IsFrontendService && !service.IsStopping && Projects.Contains(project) &&
        _runners.TryGetValue(project.Id, out var current) && current.Contains(service) &&
        service.Runner.CanOpenReadyFrontend(generation);

    private async Task OpenReadyFrontendAsync(ProjectProfile project, ServiceViewModel service, string url, long generation)
    {
        if (!CanOpenFrontend(project, service, generation)) return;
        if (_frontendBrowserRequests.Remove(service.Runner, out var prior)) prior.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_frontendBrowserLifetime.Token);
        request.CancelAfter(TimeSpan.FromSeconds(25));
        _frontendBrowserRequests[service.Runner] = request;
        service.SetBrowserOpenStatus("Checking existing browser tabs…");
        var monitor = MonitorFrontendRequestAsync(project, service, generation, request);
        try
        {
            var result = await FrontendBrowserLauncher.EnsureOpenAsync(url, async token =>
            {
                token.ThrowIfCancellationRequested();
                return await Dispatcher.InvokeAsync(() => CanOpenFrontend(project, service, generation),
                    DispatcherPriority.Background, token);
            }, request.Token);
            if (!CanOpenFrontend(project, service, generation)) return;
            service.SetBrowserOpenStatus(result.Status);
            Notice = $"{service.Name}: {result.Status}";
        }
        catch (OperationCanceledException)
        {
            if (CanOpenFrontend(project, service, generation))
            {
                const string status = "Browser tab checking timed out. Automatic opening was skipped.";
                service.SetBrowserOpenStatus(status);
                Notice = $"{service.Name}: {status}";
            }
        }
        catch
        {
            if (CanOpenFrontend(project, service, generation))
            {
                const string status = "Browser tab access was unavailable. Automatic opening was skipped.";
                service.SetBrowserOpenStatus(status);
                Notice = $"{service.Name}: {status}";
            }
        }
        finally
        {
            request.Cancel();
            await monitor;
            if (_frontendBrowserRequests.TryGetValue(service.Runner, out var active) && ReferenceEquals(active, request))
                _frontendBrowserRequests.Remove(service.Runner);
        }
    }

    private async Task MonitorFrontendRequestAsync(ProjectProfile project, ServiceViewModel service,
        long generation, CancellationTokenSource request)
    {
        try
        {
            while (!request.IsCancellationRequested)
            {
                await Task.Delay(100, request.Token);
                if (!CanOpenFrontend(project, service, generation)) request.Cancel();
            }
        }
        catch (OperationCanceledException) { }
    }
}
