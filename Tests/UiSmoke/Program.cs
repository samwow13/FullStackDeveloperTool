using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FullStackLauncher;
using FullStackLauncher.Services;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var trace = new StringWriter();
        PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(trace));
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        var window = new MainWindow();
        window.Show();
        Pump(TimeSpan.FromSeconds(4));
        Require(window.Services.Count == 2, "Both configured services appear");
        Require(window.DeveloperTools.Any(t => t.Profile.Kind == "PgAdmin"), "Existing profiles gain the pgAdmin shortcut");
        var exercise = args.Contains("--exercise");
        if (!exercise) Require(window.RootPath.EndsWith("CascadeDigitalSolutionsCRM"), "Source-relative root resolves");
        Require(window.Services.All(s => !s.CanOpen || Uri.IsWellFormedUriString(s.Url, UriKind.Absolute)), "Open buttons have valid live addresses");
        var output = Path.Combine(Environment.CurrentDirectory, "artifacts");
        Directory.CreateDirectory(output);
        if (exercise) ExerciseButtons(window);
        Render(window, Path.Combine(output, "dashboard.png"));
        window.Width = window.MinWidth;
        window.Height = window.MinHeight;
        Pump(TimeSpan.FromMilliseconds(200));
        Render(window, Path.Combine(output, "dashboard-minimum.png"));
        var store = new SettingsStore();
        var editor = new ProfileEditorWindow(window.SelectedProject!, store.BaseDirectory) { Owner = window };
        editor.Show();
        Pump(TimeSpan.FromMilliseconds(200));
        Render(editor, Path.Combine(output, "project-settings.png"));
        var originalName = window.SelectedProject!.Name;
        var nameBox = (TextBox)editor.FindName("ProjectNameBox");
        nameBox.Text = "Uncommitted edit";
        editor.Close();
        Require(window.SelectedProject.Name == originalName, "Cancel preserves the saved profile");
        var toolsEditor = new DeveloperToolsWindow(window.DeveloperTools.Select(t => t.Profile), store.BaseDirectory) { Owner = window };
        toolsEditor.Show();
        Pump(TimeSpan.FromMilliseconds(200));
        Render(toolsEditor, Path.Combine(output, "developer-tools.png"));
        var firstTool = window.DeveloperTools.First().Profile;
        var originalToolName = firstTool.Name;
        toolsEditor.Result.First().Name = "Uncommitted tool edit";
        toolsEditor.Close();
        Require(firstTool.Name == originalToolName, "Cancel preserves developer-tool settings");
        Require(trace.ToString().Length == 0, "WPF bindings have no errors: " + trace);
        window.Close();
        Pump(TimeSpan.FromMilliseconds(500));
        app.Shutdown();
        Console.WriteLine("UI smoke checks passed; rendered dashboard and settings screenshots.");
        return 0;
    }
    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }
    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
    private static void Render(Window window, string path)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void ExerciseButtons(MainWindow window)
    {
        var first = window.Services[0];
        var second = window.Services[1];
        Click(window, first, "▶  Start");
        Until(() => first.IsRunning && !first.IsBusy, "Start button reaches running");
        Require(first.CanOpen && first.Url.EndsWith("/swagger"), "Live URL button is enabled with UI path");
        var pids = first.Runner.Snapshot.ProcessIds.ToArray();
        Click(window, first, "↻  Restart");
        Until(() => first.IsRunning && !first.IsBusy && !first.Runner.Snapshot.ProcessIds.Intersect(pids).Any(), "Restart button replaces the process tree");
        Click(window, first, "Force stop");
        Until(() => !first.Runner.HasManagedProcess && !first.IsStopping, "Force stop button shuts down service");
        Click(window, first, "Setup");
        Until(() => first.IsBusy && first.CanStop, "Force stop remains available during Setup");
        Click(window, second, "▶  Start");
        Until(() => second.IsRunning && !second.IsBusy, "Another service reaches running while Setup is busy");
        Click(window, first, "Force stop");
        Until(() => !first.IsBusy && !first.IsStopping && !first.Runner.HasManagedProcess, "Force stop cancels ongoing Setup");
        Click(window, second, "Force stop");
        Until(() => !second.IsStopping && !second.Runner.HasManagedProcess, "Second service cleanup succeeds");
    }

    private static void Click(DependencyObject root, object context, string label)
    {
        var button = Children(root).OfType<Button>().Single(b => ReferenceEquals(b.DataContext, context) && Equals(b.Content, label));
        Require(button.IsEnabled, label + " button is enabled");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Children(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private static void Until(Func<bool> condition, string label)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition() && DateTime.UtcNow < deadline) Pump(TimeSpan.FromMilliseconds(100));
        Require(condition(), label);
    }
}
