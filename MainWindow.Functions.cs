using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class MainWindow
{
    private bool _resettingCodex;

    private void FunctionsMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_closeRequested || _closing || _resettingCodex) return;
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private async void HardResetCodex_Click(object sender, RoutedEventArgs e)
    {
        if (_closeRequested || _closing || _resettingCodex) return;
        if (_restartingMonitor)
        {
            Notice = "Wait for the Codex watcher to finish restarting, then try Hard Reset Codex again.";
            return;
        }

        _resettingCodex = true;
        FunctionsMenuButton.IsEnabled = false;
        HardResetCodexMenuItem.IsEnabled = false;
        try
        {
            Notice = "Finding Codex and ChatGPT processes and preparing their restart…";
            var plan = await CodexResetService.PrepareAsync();
            if (!plan.CanExecute)
            {
                Notice = "Codex reset could not be prepared. No processes were stopped.";
                MessageBox.Show(this, string.Join(Environment.NewLine,
                    new[] { Notice }.Concat(plan.Issues)), "Hard Reset Codex",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (MessageBox.Show(this, plan.ConfirmationText, "Hard Reset Codex",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                Notice = "Codex reset canceled.";
                return;
            }

            var progress = new Progress<string>(message => Notice = message);
            var result = await CodexResetService.ExecuteAsync(plan, progress: progress);
            Notice = result.Summary;
            if (!result.Succeeded)
            {
                MessageBox.Show(this, string.Join(Environment.NewLine,
                    new[] { result.Summary }.Concat(result.Issues)), "Codex reset incomplete",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            Notice = $"Codex reset did not finish: {ex.Message}";
            MessageBox.Show(this, Notice, "Codex reset incomplete",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _resettingCodex = false;
            FunctionsMenuButton.IsEnabled = true;
            HardResetCodexMenuItem.IsEnabled = true;
        }
    }
}
