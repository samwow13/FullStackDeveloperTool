using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FullStackLauncher.Models;
using FullStackLauncher.Services;
using FullStackLauncher.ViewModels;
using Microsoft.Win32;

namespace FullStackLauncher;

public partial class MainWindow
{
    private async void OpenServiceTool_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement button || ServiceFrom(sender) is not { } service || _closing) return;
        if (button.ContextMenu is { IsOpen: true }) return;

        // Keep the folder tied to this card even if selection changes during discovery.
        var folder = service.Directory;
        var configured = _settings.DeveloperTools
            .Where(tool => tool.Kind.Equals("Application", StringComparison.OrdinalIgnoreCase))
            .Select(tool => new DeveloperTool { Id = tool.Id, Name = tool.Name, Kind = tool.Kind, Target = tool.Target })
            .ToArray();
        var menu = new ContextMenu
        {
            PlacementTarget = button,
            Placement = PlacementMode.Bottom,
            Style = (Style)FindResource("ServiceToolContextMenu")
        };
        var explorer = ServiceToolMenuItem("Open in File Explorer");
        explorer.ToolTip = folder;
        explorer.Click += async (_, _) => await OpenServiceFolderInExplorerAsync(service, folder);
        menu.Items.Add(explorer);
        menu.Items.Add(ServiceToolMenuSeparator());
        var discoveryIndex = menu.Items.Count;
        menu.Items.Add(ServiceToolMenuItem("Finding installed editors…", enabled: false));
        menu.Items.Add(ServiceToolMenuSeparator());
        var browse = ServiceToolMenuItem("Choose another application…");
        browse.ToolTip = "Choose an installed .exe that accepts a folder to open.";
        browse.Click += (_, _) => BrowseServiceTool(service, folder);
        menu.Items.Add(browse);
        var manage = ServiceToolMenuItem("Manage saved tools…");
        manage.Click += ManageTools_Click;
        menu.Items.Add(manage);
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(button.ContextMenu, menu)) button.ClearValue(ContextMenuProperty);
        };
        button.ContextMenu = menu;
        menu.IsOpen = true;

        try
        {
            var choices = await Task.Run(() =>
            {
                var found = DeveloperToolLauncher.FindInstalledEditors()
                    .Select(editor => (Name: editor.Name, Target: (DeveloperToolTarget?)new(editor.FileName, false, editor.Name)))
                    .ToList();
                foreach (var tool in configured)
                {
                    try { found.Add((tool.Name, DeveloperToolLauncher.Resolve(tool, _store.BaseDirectory))); }
                    catch { found.Add((tool.Name, null)); }
                }
                return found;
            });
            if (_closing || !menu.IsOpen || !button.IsLoaded || !ReferenceEquals(button.DataContext, service))
            {
                menu.IsOpen = false;
                return;
            }

            menu.Items.RemoveAt(discoveryIndex);
            var insertAt = discoveryIndex;
            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, target) in choices)
            {
                if (target is null)
                {
                    var missing = ServiceToolMenuItem($"{name} (unavailable)", enabled: false);
                    missing.ToolTip = "Edit this application's location in Manage tools.";
                    ToolTipService.SetShowOnDisabled(missing, true);
                    menu.Items.Insert(insertAt++, missing);
                    continue;
                }
                if (!added.Add(target.FileName)) continue;
                var item = ServiceToolMenuItem(name);
                item.ToolTip = target.FileName;
                item.Click += (_, _) => OpenServiceFolder(service, folder, target);
                menu.Items.Insert(insertAt++, item);
            }
            if (choices.Count == 0)
                menu.Items.Insert(discoveryIndex, ServiceToolMenuItem("No editors detected. Choose an application below.", enabled: false));
        }
        catch
        {
            if (!menu.IsOpen || _closing) return;
            menu.Items[discoveryIndex] = ServiceToolMenuItem("Editor discovery unavailable. Choose an application below.", enabled: false);
        }
    }

    private Separator ServiceToolMenuSeparator() => new()
    {
        Style = (Style)FindResource("ServiceToolMenuSeparator")
    };

    private MenuItem ServiceToolMenuItem(string label, bool enabled = true)
    {
        var item = new MenuItem
        {
            Header = new TextBlock { Text = label, MaxWidth = 430, TextWrapping = TextWrapping.Wrap },
            Style = (Style)FindResource("ServiceToolMenuItem"),
            IsEnabled = enabled
        };
        AutomationProperties.SetName(item, label);
        return item;
    }

    private void BrowseServiceTool(ServiceViewModel service, string folder)
    {
        if (_closing) return;
        var dialog = new OpenFileDialog
        {
            Title = $"Open {service.Name} in an application",
            Filter = "Windows applications (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        OpenServiceFolder(service, folder, new(dialog.FileName, false, Path.GetFileNameWithoutExtension(dialog.FileName)));
    }

    private async Task OpenServiceFolderInExplorerAsync(ServiceViewModel service, string folder)
    {
        if (_closing) return;
        try
        {
            await Task.Run(() =>
            {
                var resolvedFolder = Path.GetFullPath(folder);
                if (!Directory.Exists(resolvedFolder)) throw new DirectoryNotFoundException();
                var windowsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (string.IsNullOrWhiteSpace(windowsFolder)) throw new FileNotFoundException();
                var explorerPath = Path.Combine(windowsFolder, "explorer.exe");
                if (!File.Exists(explorerPath)) throw new FileNotFoundException();
                var startInfo = new ProcessStartInfo(explorerPath) { UseShellExecute = false };
                startInfo.ArgumentList.Add(resolvedFolder);
                using var explorer = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("File Explorer did not start.");
            });
            if (_closing) return;
            Notice = $"Opened {service.Name}'s folder in File Explorer.";
        }
        catch
        {
            if (_closing) return;
            const string message = "The service folder could not be opened in File Explorer. Check that its configured working folder exists and File Explorer is available.";
            Notice = message;
            MessageBox.Show(this, message, "Open service folder", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenServiceFolder(ServiceViewModel service, string folder, DeveloperToolTarget target)
    {
        if (_closing) return;
        try
        {
            // Executables within a service folder are eligible for its existing process tracking.
            // Installed tools must stay outside all configured service folders, including archived projects.
            var executable = Path.GetFullPath(Environment.ExpandEnvironmentVariables(target.FileName));
            if (_runners.Values.SelectMany(services => services).Any(candidate => executable.StartsWith(
                Path.GetFullPath(candidate.Directory).TrimEnd('\\', '/') + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)))
            {
                const string locationMessage = "Choose an application installed outside your configured service folders so service Stop and Restart actions cannot affect it.";
                Notice = locationMessage;
                MessageBox.Show(this, locationMessage, "Open service folder", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            DeveloperToolLauncher.OpenFolder(target, folder);
            Notice = $"Opened {service.Name}'s folder in {target.Description}.";
        }
        catch
        {
            const string message = "The service folder could not be opened. Check that its working folder and the selected application still exist, and that the application accepts a folder argument.";
            Notice = message;
            MessageBox.Show(this, message, "Open service folder", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
