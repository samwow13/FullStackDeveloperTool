using System.Windows;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class LanComputersWindow
{
    private readonly LanLocalCompanionInstaller _localCompanionInstaller = new();

    private async void InstallAgent_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _closed) return;
        await RunAsync(async token =>
        {
            using var package = await LanCompanionPackage.StageAsync(token);
            var result = await Task.Run(() => _localCompanionInstaller.InstallAsync(package.ExecutablePath, token), token);
            if (_closed) return;
            SetStatus(result.Text);
        }, $"Installing agent on this PC ({Environment.MachineName})…", cancellationStatus: "Agent installation canceled before local setup started.");
    }
}
