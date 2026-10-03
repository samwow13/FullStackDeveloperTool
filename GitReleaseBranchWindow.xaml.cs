using System.Windows;
using System.Windows.Controls;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class GitReleaseBranchWindow : Window
{
    private readonly IReadOnlyList<GitRemoteBranchChoice> _branches;
    public GitReleaseBranchWindow(string root, string remote, string fetchUrl, IReadOnlyList<GitRemoteBranchChoice> branches, string? selected)
    {
        _branches = branches;
        InitializeComponent();
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        Context.Text = SensitiveDataProtection.Redact($"Repository: {root}\nSource remote: {remote}\n{fetchUrl}\n\nThese branches were read from the remote just now. Saving this choice does not merge or push anything.");
        Branches.ItemsSource = branches;
        Branches.SelectedItem = branches.FirstOrDefault(branch => branch.Name == selected);
        EmptyNotice.Visibility = branches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => Search.Focus();
    }
    public string? SelectedBranch => (Branches.SelectedItem as GitRemoteBranchChoice)?.Name;
    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (Branches == null) return;
        var selected = SelectedBranch;
        var filtered = _branches.Where(branch => branch.Name.Contains(Search.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        Branches.ItemsSource = filtered;
        Branches.SelectedItem = filtered.FirstOrDefault(branch => branch.Name == selected);
        EmptyNotice.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Branch_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Submit != null) Submit.IsEnabled = SelectedBranch != null;
    }
    private void Submit_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedBranch != null) DialogResult = true;
    }
}
