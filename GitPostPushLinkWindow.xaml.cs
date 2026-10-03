using System.Windows;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class GitPostPushLinkWindow : Window
{
    private readonly GitRemoteInfo _remote;
    public string Link { get; private set; } = "";
    public string Draft => LinkInput.Text;

    public GitPostPushLinkWindow(string root, GitRemoteInfo remote, string? draft = null)
    {
        _remote = remote;
        InitializeComponent();
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        ConnectionText.Text = $"Repository: {root}\nConnection: {remote.Name}\nPush destination: {remote.PushUrl}";
        LinkInput.Text = draft ?? PostPushLink.Url(remote);
        ValidationText.Text = remote.PostPushLinkError ?? "";
        Loaded += (_, _) => LinkInput.Focus();
    }

    private void UseDefault_Click(object sender, RoutedEventArgs e) => LinkInput.Text = PostPushLink.DefaultUrl(_remote);

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try { Link = PostPushLink.Validate(LinkInput.Text); DialogResult = true; }
        catch (InvalidOperationException exception) { ValidationText.Text = exception.Message; LinkInput.Focus(); }
    }
}
