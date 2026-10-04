using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class FirstTimeSetupWindow : Window
{
    private readonly FirstTimeSetupContent _content;
    private bool _connecting;

    internal FirstTimeSetupWindow(FirstTimeSetupContent content)
    {
        _content = content;
        InitializeComponent();
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        AgentsBox.Text = content.AgentsInstructions;
        PromptBox.Text = content.SetupPrompt;
        ManualBox.Text = content.ManualInstructions;
        CommandBox.Text = content.CodexCommand;
        Loaded += (_, _) => CopyAgentsButton.Focus();
    }

    private void CopyAgents_Click(object sender, RoutedEventArgs e) => Copy(_content.AgentsInstructions,
        "Instructions copied. Add them to your existing AGENTS.md without replacing its current content.");

    private void CopyPrompt_Click(object sender, RoutedEventArgs e) => Copy(_content.SetupPrompt,
        "Setup prompt copied. Paste it into your agent chat after adding the AGENTS.md instructions.");

    private void CopyManual_Click(object sender, RoutedEventArgs e) => Copy(_content.ManualInstructions,
        "Connection details copied. Add a local STDIO server in your agent client's MCP settings.");

    private void CopyCommand_Click(object sender, RoutedEventArgs e) => Copy(_content.CodexCommand,
        "PowerShell command copied. Review the existing fullStackLauncher entry before running it.");

    private void Copy(string value, string message)
    {
        try
        {
            Clipboard.SetText(value);
            SetStatus(message, success: true);
        }
        catch (Exception)
        {
            SetStatus("The clipboard is busy. Try copying again, or select and copy the preview text.", error: true);
        }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_connecting) return;
        _connecting = true;
        ConnectButton.IsEnabled = false;
        CloseButton.IsEnabled = false;
        SetStatus("Checking Codex MCP settings and registering this launcher…");
        try
        {
            var result = await CodexMcpSetup.ConnectAsync(_content);
            SetStatus(result.Message, success: result.Succeeded, error: !result.Succeeded);
        }
        catch (Exception)
        {
            SetStatus("Connection setup did not finish. Inspect fullStackLauncher in Codex MCP settings before retrying. Use the manual connection details if needed.", error: true);
        }
        finally
        {
            _connecting = false;
            ConnectButton.IsEnabled = true;
            CloseButton.IsEnabled = true;
        }
    }

    private void SetStatus(string message, bool success = false, bool error = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = error ? new SolidColorBrush(Color.FromRgb(255, 188, 139))
            : (Brush)FindResource(success ? "AccentBrush" : "MutedBrush");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_connecting) return;
        e.Cancel = true;
        SetStatus("Wait for connection setup to finish before closing. Registration may already be in progress.");
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }
}
