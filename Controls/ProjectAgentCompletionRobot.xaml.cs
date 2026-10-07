using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FullStackLauncher.Controls;

/// <summary>A compact completion celebration that remains readable without motion.</summary>
public partial class ProjectAgentCompletionRobot : UserControl
{
    private readonly List<(IAnimatable Target, DependencyProperty Property)> _animations = [];
    private bool _observing;
    private Window? _window;

    public ProjectAgentCompletionRobot()
    {
        InitializeComponent();
        Loaded += Robot_Loaded;
        Unloaded += Robot_Unloaded;
        IsVisibleChanged += Robot_IsVisibleChanged;
    }

    private void Robot_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_observing)
        {
            _observing = true;
            SystemParameters.StaticPropertyChanged += MotionPreferenceChanged;
            LayoutUpdated += Robot_LayoutUpdated;
            _window = Window.GetWindow(this);
            if (_window != null) _window.StateChanged += Window_StateChanged;
        }
        RefreshMotion();
    }

    private void Robot_Unloaded(object sender, RoutedEventArgs e)
    {
        StopMotion();
        if (!_observing) return;
        _observing = false;
        SystemParameters.StaticPropertyChanged -= MotionPreferenceChanged;
        LayoutUpdated -= Robot_LayoutUpdated;
        if (_window != null) _window.StateChanged -= Window_StateChanged;
        _window = null;
    }

    private void Robot_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => RefreshMotion();
    private void Robot_LayoutUpdated(object? sender, EventArgs e) => RefreshMotion();
    private void Window_StateChanged(object? sender, EventArgs e) => RefreshMotion();

    private void MotionPreferenceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (null or "" or nameof(SystemParameters.ClientAreaAnimation))) return;
        if (Dispatcher.CheckAccess()) RefreshMotion();
        else if (!Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(new Action(() => { if (_observing) RefreshMotion(); }));
    }

    private void RefreshMotion()
    {
        // Match the Crew robot's motion policy, including scrolled-out and minimized windows.
        var shouldAnimate = _observing && IsLoaded && IsVisible && SystemParameters.ClientAreaAnimation
            && _window?.WindowState != WindowState.Minimized && IsInsideViewport();
        if (!shouldAnimate) { StopMotion(); return; }
        if (_animations.Count != 0) return;

        AnimateValue(DanceTilt, RotateTransform.AngleProperty, -6, 6, 0.38);
        AnimateValue(DanceBounce, TranslateTransform.YProperty, 0, -1.7, 0.19);
        AnimateValue(LeftArmSwing, RotateTransform.AngleProperty, -18, 15, 0.38);
        AnimateValue(RightArmSwing, RotateTransform.AngleProperty, -15, 18, 0.38);
        AnimateValue(LeftFootStep, RotateTransform.AngleProperty, -15, 8, 0.38);
        AnimateValue(RightFootStep, RotateTransform.AngleProperty, -8, 15, 0.38);
    }

    private bool IsInsideViewport()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return false;
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        for (DependencyObject? ancestor = VisualTreeHelper.GetParent(this); ancestor != null;
             ancestor = VisualTreeHelper.GetParent(ancestor))
        {
            if (ancestor is not FrameworkElement element || (!element.ClipToBounds && element is not ScrollContentPresenter)) continue;
            var visibleBounds = TransformToAncestor(element).TransformBounds(bounds);
            if (!visibleBounds.IntersectsWith(new Rect(0, 0, element.ActualWidth, element.ActualHeight))) return false;
        }
        return true;
    }

    private void AnimateValue(IAnimatable target, DependencyProperty property, double from, double to, double seconds)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Timeline.SetDesiredFrameRate(animation, 24);
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        _animations.Add((target, property));
    }

    private void StopMotion()
    {
        foreach (var (target, property) in _animations) target.BeginAnimation(property, null);
        _animations.Clear();
    }
}
