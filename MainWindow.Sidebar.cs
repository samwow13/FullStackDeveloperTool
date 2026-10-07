using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace FullStackLauncher;

public partial class MainWindow
{
    private readonly DispatcherTimer _sidebarCollapseTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _sidebarRevealed;

    public bool SidebarAutoHide
    {
        get => _settings.Layout.SidebarAutoHide;
        set
        {
            if (_settings.Layout.SidebarAutoHide == value) return;
            _sidebarCollapseTimer.Stop();
            _settings.Layout.SidebarAutoHide = value;
            _sidebarRevealed = false;
            if (value && SidebarPanel.IsKeyboardFocusWithin) SectionsMenuToggle.Focus();
            ApplySidebarLayout();
            Changed(nameof(SidebarAutoHide));
            Changed(nameof(SidebarModeLabel));
            Changed(nameof(SidebarModeDescription));
            SaveLayout();
        }
    }

    public bool IsSidebarCompact => _settings.Layout.SidebarAutoHide && !_sidebarRevealed;
    public string SidebarModeLabel => _settings.Layout.SidebarAutoHide ? "Pin" : "Auto-hide";
    public string SidebarModeDescription => _settings.Layout.SidebarAutoHide
        ? "Keep projects and tools expanded."
        : "Minimize projects and tools to a status rail. Hover or focus the rail to expand it.";

    private void InitializeSidebarAutoHide()
    {
        _sidebarCollapseTimer.Tick += (_, _) =>
        {
            _sidebarCollapseTimer.Stop();
            if (SidebarPanel.IsMouseOver || SidebarPanel.IsKeyboardFocusWithin) return;
            RevealSidebar(false);
        };
        Deactivated += (_, _) => ScheduleSidebarCollapse();
        Closed += (_, _) => _sidebarCollapseTimer.Stop();
    }

    private void ApplySidebarLayout()
    {
        var visible = ProjectsVisible || ToolsVisible;
        // Hover expansion overlays the workspace; its content never changes width.
        SidebarColumn.Width = new GridLength(!visible ? 0 : _settings.Layout.SidebarAutoHide ? 68 : 245);
        SidebarPanel.Width = IsSidebarCompact ? 68 : 245;
        SidebarContent.Margin = IsSidebarCompact ? new Thickness(8, 24, 8, 18) : new Thickness(16, 24, 16, 18);
        Changed(nameof(IsSidebarCompact));
    }

    private void RevealSidebar(bool revealed)
    {
        _sidebarCollapseTimer.Stop();
        if (!_settings.Layout.SidebarAutoHide || _sidebarRevealed == revealed) return;
        _sidebarRevealed = revealed;
        ApplySidebarLayout();
    }

    private void ScheduleSidebarCollapse()
    {
        if (!_settings.Layout.SidebarAutoHide) return;
        _sidebarCollapseTimer.Stop();
        _sidebarCollapseTimer.Start();
    }

    private void Sidebar_MouseEnter(object sender, MouseEventArgs e) => RevealSidebar(true);
    private void Sidebar_MouseLeave(object sender, MouseEventArgs e) => ScheduleSidebarCollapse();
    private void Sidebar_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => RevealSidebar(true);
    private void Sidebar_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => ScheduleSidebarCollapse();

    private void SidebarMode_Click(object sender, RoutedEventArgs e)
    {
        SidebarAutoHide = !SidebarAutoHide;
    }
}
