using System.Diagnostics;
using System.Windows;
using System.Windows.Controls.Primitives;
using FullStackLauncher.Models;
using FullStackLauncher.Services;
using FullStackLauncher.ViewModels;

namespace FullStackLauncher;

public partial class MainWindow
{
    private void ChangeDatabase_Click(object sender, RoutedEventArgs e)
    {
        // Header actions must not toggle the enclosing database expander.
        e.Handled = true;
        if (ServiceFrom(sender) is not { CanChangeDatabase: true } service ||
            _closing || _closed || _closeRequested || _batchBusy)
        {
            if (sender is ToggleButton inactive) inactive.IsChecked = false;
            return;
        }

        ChangeDatabaseWindow? editor = null;
        try
        {
            editor = new ChangeDatabaseWindow(service.Profile, service.Directory,
                (configuration, key, value) => SaveDatabaseAndRestartAsync(service, configuration, key, value, editor!),
                service.ObserveDatabaseConfiguration)
                { Owner = this };
            editor.ShowDialog();
        }
        catch (ApiSecretStoreException ex) { Notice = ex.Message; }
        catch (Exception) { Notice = "The Change DB editor could not open. Check the API working folder and retry."; }
        finally
        {
            if (sender is ToggleButton toggle) toggle.IsChecked = false;
            (sender as UIElement)?.Focus();
        }
    }

    private async Task<bool> SaveDatabaseAndRestartAsync(ServiceViewModel service,
        ApiDatabaseConfiguration configuration, string key, string value, ChangeDatabaseWindow editor)
    {
        if (!service.CanChangeDatabase || _closing || _closed || _closeRequested || _batchBusy)
        {
            editor.SetStatus("The API cannot change configuration right now. Wait for its current action to finish, then retry.", error: true);
            return false;
        }

        var previousRun = service.Runner.ManagedApiRunVersion;
        var initialStopRevision = service.Runner.StopRevision;
        var saved = false;
        var verified = false;
        string? expectedName = null;
        var failure = "The database configuration could not be saved. Your draft is retained.";
        await RunActionAsync(service, async runner =>
        {
            try
            {
                RequireCurrentService(initialStopRevision);
                editor.SetStatus("Validating the database connection…");
                var change = await Task.Run(() => configuration.Prepare(key, value));
                expectedName = change.DatabaseName;
                if (expectedName is null)
                    throw new ApiSecretStoreException("Enter a connection string with one valid Database or Initial Catalog name before saving.");
                RequireCurrentService(initialStopRevision);
                editor.SetStatus("Saving encrypted Local API configuration…");
                await Task.Run(() => configuration.Save(change));
                saved = true;
                service.Runner.ConfigurationNeedsRestart = true;
                service.ObserveDatabaseConfiguration(configuration);
                editor.MarkConfigurationSaved();
                service.BeginDatabaseChange(expectedName);
                foreach (var other in _runners.Values.SelectMany(list => list).Where(item =>
                             item.Directory.Equals(service.Directory, StringComparison.OrdinalIgnoreCase)))
                {
                    if (other.Runner.AppliedConfigurationEnvironment != "Prod")
                        other.Runner.ConfigurationNeedsRestart = true;
                    other.Update();
                }
                failure = "Configuration saved. The API restart did not complete; the database change is unverified. Your draft is retained for retry.";
                var launch = await Task.Run(() => configuration.PrepareLaunch(change));
                RequireCurrentService(initialStopRevision);

                var previousSelection = service.Profile.ApiConfiguration;
                if (previousSelection is null)
                {
                    service.Profile.ApiConfiguration = new() { Environment = "Local" };
                    try { _store.Save(_settings); }
                    catch (Exception)
                    {
                        service.Profile.ApiConfiguration = previousSelection;
                        failure = "Encrypted configuration saved, but the Local API selection could not be saved. The API was not restarted. Reopen the launcher and retry.";
                        throw new ApiSecretStoreException(failure);
                    }
                    service.Update();
                }

                editor.SetStatus($"Restarting {service.Name}…");
                var restart = runner.ApplyConfigurationAsync(launch);
                var restartRevision = runner.StopRevision;
                await restart;
                if (runner.Snapshot.State is ServiceState.Error or ServiceState.Conflict ||
                    runner.ManagedApiRunVersion <= previousRun)
                    throw new ApiSecretStoreException(failure);
                editor.SetStatus("API restarting. Waiting for a successful database health check…");
                var result = await WaitForChangedDatabaseAsync(service, expectedName, previousRun, restartRevision);
                if (result is not null)
                {
                    failure = result;
                    throw new ApiSecretStoreException(result);
                }
                verified = true;
                service.FinishDatabaseChange(true, "");
            }
            catch (ApiSecretStoreException ex)
            {
                failure = saved && !ex.Message.StartsWith("Configuration saved", StringComparison.Ordinal) &&
                          !ex.Message.StartsWith("Encrypted configuration saved", StringComparison.Ordinal)
                    ? $"Configuration saved; database change unverified. {ex.Message}" : ex.Message;
                throw new InvalidOperationException(failure);
            }
            catch (Exception)
            {
                // Never surface an arbitrary parser/provider exception from a secret editing flow.
                throw new InvalidOperationException(failure);
            }
        }, "Applying database change");

        if (verified)
        {
            Notice = $"Database change completed: {expectedName}. API restarted and its read-only database query passed.";
            editor.SetStatus(Notice);
            return true;
        }
        if (saved) service.FinishDatabaseChange(false, failure);
        Notice = failure;
        editor.SetStatus(failure, error: true);
        return false;

        void RequireCurrentService(int stopRevision)
        {
            if (_closing || _closed || _closeRequested || !IsKnownService() || service.ProductionWarning ||
                service.Runner.StopRevision != stopRevision ||
                !configuration.WorkingDirectory.Equals(service.Directory, StringComparison.OrdinalIgnoreCase))
                throw new ApiSecretStoreException("The API was stopped or its configuration changed. Close this editor and reopen Change DB before retrying.");
        }

        bool IsKnownService() => _runners.Values.Any(list => list.Contains(service));
    }

    private async Task<string?> WaitForChangedDatabaseAsync(ServiceViewModel service, string expectedName,
        long previousRun, int restartRevision)
    {
        var elapsed = Stopwatch.StartNew();
        long? newRun = null;
        string? observedName = null;
        while (elapsed.Elapsed < TimeSpan.FromSeconds(90))
        {
            if (_closing || _closed || _closeRequested || service.IsStopping ||
                service.Runner.StopRevision != restartRevision || service.ProductionWarning)
                return "Configuration saved. Database verification canceled by a service stop or configuration change. The database change is unverified.";
            await service.Runner.RefreshAsync();
            service.Update();
            var snapshot = service.Runner.Snapshot;
            if (snapshot.State is ServiceState.Error or ServiceState.Conflict)
                return "Configuration saved. The API could not restart successfully. Check its service output and retry Change DB; the database change is unverified.";
            var runVersion = service.Runner.ManagedApiRunVersion;
            if (runVersion > previousRun)
            {
                newRun ??= runVersion;
                if (newRun != runVersion)
                    return "Configuration saved. Another API run replaced this restart. Reopen Change DB to verify the current configuration.";
                if (snapshot.State == ServiceState.Running && snapshot.IsManaged &&
                    service.Runner.AppliedConfigurationEnvironment == "Local")
                {
                    await service.RefreshApiDatabaseAsync(force: true);
                    service.Update();
                    if (service.Runner.StopRevision != restartRevision || service.Runner.ManagedApiRunVersion != newRun ||
                        service.IsStopping || _closing || _closed || _closeRequested) continue;
                    observedName = service.VerifiedDatabaseName;
                    if (string.Equals(observedName, expectedName, StringComparison.Ordinal)) return null;
                }
            }
            await Task.Delay(1000);
        }
        return observedName is not null
            ? $"Configuration saved. The API reported database {observedName}, but expected {expectedName}. The database change is unverified; check the selected connection key and retry."
            : "Configuration saved. The API did not confirm the requested database within 90 seconds. Check its service output and /health/database support, then retry; the database change is unverified.";
    }
}
