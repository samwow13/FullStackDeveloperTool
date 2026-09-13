using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace FullStackLauncher.CodexMonitor;

/// <summary>A passive, always-on-top view. Monitoring and persisted preferences belong to the alerts window.</summary>
public partial class ChatProgressWindow : Window
{
    private bool _allowClose;

    public event EventHandler? HideRequested;
    public event Action<string?>? ClearCompletedRequested;
    public event EventHandler? BoundsChanged;

    public ChatProgressWindow()
    {
        InitializeComponent();
    }

    public void UpdateRows(IReadOnlyList<ChatProgressRow> rows)
    {
        // The state model aggregates each parent chat with its agents and retains finished rows.
        // Updating the view must never activate the window or change its visibility.
        ChatList.ItemsSource = rows;
        ClearAllButton.IsEnabled = rows.Any(row => row.IsCompleted);
    }

    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    private void Chat_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Keep interactive controls usable if they are later added within a draggable row.
        for (var element = e.OriginalSource as DependencyObject; element is not null;
             element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
        {
            if (element is Button) return;
            if (ReferenceEquals(element, sender)) break;
        }
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); }
        catch (InvalidOperationException) { /* The mouse can be released between the routed event and DragMove. */ }
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e) => ClearCompletedRequested?.Invoke(null);

    private void ResizeGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        Width = Math.Clamp(ActualWidth + e.HorizontalChange, MinWidth, MaxWidth);
        Height = Math.Clamp(ActualHeight + e.VerticalChange, MinHeight, MaxHeight);
    }

    private void RequestHide()
    {
        Hide();
        HideRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        RequestHide();
    }

    private void Window_BoundsChanged(object? sender, EventArgs e)
    {
        if (IsLoaded && WindowState == WindowState.Normal)
            BoundsChanged?.Invoke(this, EventArgs.Empty);
    }
}
