using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using FullStackLauncher.Models;
using FullStackLauncher.Services;
using FullStackLauncher.ViewModels;
using Microsoft.Win32;

namespace FullStackLauncher;

public partial class MainWindow
{
    private async void OpenServiceTool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || ServiceFrom(sender) is not { } service || _closing) return;
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
            Background = (Brush)FindResource("SurfaceBrush"),
            Foreground = (Brush)FindResource("TextBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4)
        };
        menu.Items.Add(ServiceToolMenuItem("Finding installed editors…", enabled: false));
        menu.Items.Add(new Separator());
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

            menu.Items.RemoveAt(0);
            var insertAt = 0;
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
                menu.Items.Insert(0, ServiceToolMenuItem("No editors detected. Choose an application below.", enabled: false));
        }
        catch
        {
            if (!menu.IsOpen || _closing) return;
            menu.Items[0] = ServiceToolMenuItem("Editor discovery unavailable. Choose an application below.", enabled: false);
        }
    }

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
