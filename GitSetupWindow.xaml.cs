using System.Windows;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class GitSetupWindow : Window, IDashboardReplacementWindow
{
    private readonly GitWorkspaceWindow _workflow;

    internal GitSetupWindow(GitWorkspaceWindow workflow, FrameworkElement content)
    {
        _workflow = workflow;
        InitializeComponent();
        Resources.MergedDictionaries.Add(workflow.Resources);
        DataContext = workflow;
        WorkflowContent.Content = content;
        var workArea = SystemParameters.WorkArea;
        MaxHeight = workArea.Height;
        Width = Math.Min(Width, workArea.Width);
        MinWidth = Math.Min(MinWidth, workArea.Width);
        workflow.SetSetupViewport(Math.Max(120, workArea.Height - 300));
        Closing += workflow.SetupWindow_Closing;
        Loaded += (_, _) => workflow.FocusSetupStep();
    }

    public bool IsDashboardReplacementReady => _workflow.IsDashboardReplacementReady;
    public bool IsDashboardReplacementClosePending => _workflow.IsDashboardReplacementClosePending;

    internal void DetachContent() => WorkflowContent.Content = null;
    private void Refresh_Click(object sender, RoutedEventArgs e) => _workflow.RefreshSetup();
    private void Cancel_Click(object sender, RoutedEventArgs e) => _workflow.CancelSetupOperation();
    private void Trust_Click(object sender, RoutedEventArgs e) => _workflow.TrustSetupFolder();
    private void Close_Click(object sender, RoutedEventArgs e) { if (_workflow.IsIdle) Close(); }
}
