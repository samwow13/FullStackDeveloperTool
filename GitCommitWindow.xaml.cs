using System.Windows;
using System.Windows.Controls;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class GitCommitWindow : Window
{
    public GitCommitWindow(string root, string branch, string remote, string pushUrl, int changedFiles, string draft)
    {
        InitializeComponent();
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        Destination.Text = SensitiveDataProtection.Redact($"{branch} → {remote}/{branch}\n{pushUrl}");
        Repository.Text = root;
        Scope.Text = $"Stages all {changedFiles:N0} changed files, including additions and deletions, then commits and pushes this branch. Ignored untracked files stay excluded.";
        MessageBox.Text = draft;
        Loaded += (_, _) => { MessageBox.Focus(); MessageBox.CaretIndex = MessageBox.Text.Length; };
    }

    public string Message => MessageBox.Text;

    private void Message_Changed(object sender, TextChangedEventArgs e)
    {
        if (Submit != null) Submit.IsEnabled = !string.IsNullOrWhiteSpace(MessageBox.Text);
    }

    private void Submit_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(MessageBox.Text)) DialogResult = true;
    }
}
