using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FullStackLauncher.AgentBridge;

namespace FullStackLauncher;

public partial class MainWindow
{
    private Window? _agentRestartWarning;
    private TextBlock? _agentNoticeText;
    private Button? _agentNoticeDismiss;
    private readonly DispatcherTimer _agentRestartWarningTimer = new()
    {
        Interval = TimeSpan.FromSeconds(8)
    };

    private void ShowAgentRestartWarning() => ShowAgentNotice(AgentBridgeCoordination.AgentRestartMessage,
        "Dismiss restart warning");

    private void ShowAgentGitUpdateNotice() => ShowAgentNotice("Agent updated commit message",
        "Dismiss commit message update");

    private void ShowAgentNotice(string message, string dismissLabel)
    {
        if (_closeRequested || _closing || _closed) return;
        Notice = message;
        _agentRestartWarningTimer.Stop();
        if (_agentRestartWarning is null)
        {
            var warningBrush = new SolidColorBrush(Color.FromRgb(255, 205, 105));
            var content = new DockPanel { LastChildFill = true };
            var dismiss = new Button
            {
                Content = "×",
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                Margin = new Thickness(12, 0, 0, 0),
                ToolTip = dismissLabel
            };
            AutomationProperties.SetName(dismiss, dismissLabel);
            dismiss.Click += (_, _) => CloseAgentRestartWarning();
            DockPanel.SetDock(dismiss, Dock.Right);
            content.Children.Add(dismiss);
            var noticeText = new TextBlock
            {
                Text = message,
                Foreground = warningBrush,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            content.Children.Add(noticeText);
            var workArea = SystemParameters.WorkArea;
            var warning = new Window
            {
                Title = message,
                Width = 330,
                Height = 68,
                Left = Math.Max(workArea.Left, workArea.Right - 346),
                Top = Math.Max(workArea.Top, workArea.Bottom - 84),
                WindowStartupLocation = WindowStartupLocation.Manual,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowActivated = false,
                ShowInTaskbar = false,
                Topmost = true,
                Background = (Brush)FindResource("CardBrush"),
                Content = new Border
                {
                    BorderBrush = warningBrush,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(16, 12, 12, 12),
                    Child = content
                }
            };
            // An owned window would disappear with a minimized dashboard. Close this
            // independent, nonmodal warning explicitly when the dashboard closes.
            warning.Closed += (_, _) =>
            {
                _agentRestartWarningTimer.Stop();
                _agentRestartWarning = null;
                _agentNoticeText = null;
                _agentNoticeDismiss = null;
            };
            _agentRestartWarning = warning;
            _agentNoticeText = noticeText;
            _agentNoticeDismiss = dismiss;
            warning.Show();
        }
        else
        {
            _agentRestartWarning.Title = message;
            if (_agentNoticeText is not null) _agentNoticeText.Text = message;
            if (_agentNoticeDismiss is not null)
            {
                _agentNoticeDismiss.ToolTip = dismissLabel;
                AutomationProperties.SetName(_agentNoticeDismiss, dismissLabel);
            }
        }
        _agentRestartWarningTimer.Start();
    }

    private void CloseAgentRestartWarning()
    {
        _agentRestartWarningTimer.Stop();
        _agentRestartWarning?.Close();
    }
}
