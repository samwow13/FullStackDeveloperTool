using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FullStackLauncher.Controls;

/// <summary>A passive agent illustration. Its pose always conveys the state without animation.</summary>
public partial class CodexRobot : UserControl
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(string), typeof(CodexRobot), new PropertyMetadata("Idle", AppearanceChanged));

    public static readonly DependencyProperty AnimateProperty = DependencyProperty.Register(
        nameof(Animate), typeof(bool), typeof(CodexRobot), new PropertyMetadata(true, AppearanceChanged));

    private static readonly Brush RunningBrush = FrozenBrush(0x72, 0xE5, 0xC3);
    private static readonly Brush CompletedBrush = FrozenBrush(0xA3, 0xCB, 0xD4);
    private static readonly Brush WaitingBrush = FrozenBrush(0xE7, 0xC1, 0x79);
    private const double SleepingHeadTilt = 8;
    private const double SleepingHeadY = 17;
    private static readonly Brush UnknownBrush = FrozenBrush(0x8F, 0xA1, 0xB3);
    private static readonly Brush FailedBrush = FrozenBrush(0xF0, 0x9D, 0xA2);
    private static readonly Brush IdleBrush = FrozenBrush(0x8C, 0xB6, 0xBB);
    private readonly List<(IAnimatable Target, DependencyProperty Property)> _animations = [];
    private bool _initialized;
    private bool _observing;
    private Window? _window;
    private string _pose = "Idle";

    public CodexRobot()
    {
        InitializeComponent();
        _initialized = true;
        Loaded += Robot_Loaded;
        Unloaded += Robot_Unloaded;
        IsVisibleChanged += Robot_IsVisibleChanged;
        ApplyAppearance();
    }

    public string State
    {
        get => (string)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public bool Animate
    {
        get => (bool)GetValue(AnimateProperty);
        set => SetValue(AnimateProperty, value);
    }

    private static void AppearanceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((CodexRobot)sender).ApplyAppearance();

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

    private void ApplyAppearance()
    {
        if (!_initialized) return;
        StopMotion();
        _pose = State?.ToUpperInvariant() switch
        {
            "RUNNING" => "Running",
            "COMPLETED" => "Completed",
            "WAITING" => "Waiting",
            "NEEDSINPUT" => "NeedsInput",
            "FAILED" => "Failed",
            "IDLE" => "Idle",
            _ => "Unknown"
        };

        Foreground = _pose switch
        {
            "Running" => RunningBrush,
            "Completed" => CompletedBrush,
            "Waiting" or "NeedsInput" => WaitingBrush,
            "Failed" => FailedBrush,
            "Idle" => IdleBrush,
            _ => UnknownBrush
        };
        OpenEyes.Visibility = _pose is "Running" or "Idle" ? Visibility.Visible : Visibility.Collapsed;
        SleepEyes.Visibility = _pose == "Completed" ? Visibility.Visible : Visibility.Collapsed;
        WaitingEyes.Visibility = _pose == "Waiting" ? Visibility.Visible : Visibility.Collapsed;
        ExcitedEyes.Visibility = _pose == "NeedsInput" ? Visibility.Visible : Visibility.Collapsed;
        FailedEyes.Visibility = _pose == "Failed" ? Visibility.Visible : Visibility.Collapsed;
        UnknownEyes.Visibility = _pose == "Unknown" ? Visibility.Visible : Visibility.Collapsed;
        SleepMarks.Visibility = _pose == "Completed" ? Visibility.Visible : Visibility.Collapsed;
        StateMark.Visibility = _pose is "Waiting" or "NeedsInput" or "Unknown" or "Failed" ? Visibility.Visible : Visibility.Collapsed;
        StateMark.Text = _pose switch { "Waiting" => "…", "Failed" => "!", _ => "?" };

        // The lowered head's bottom edge meets the top keyboard row, even with motion disabled.
        HeadTilt.Angle = _pose switch { "Completed" => SleepingHeadTilt, "NeedsInput" => -3, "Waiting" => -5, "Unknown" => -7, _ => 0 };
        HeadMotion.X = _pose == "Completed" ? 1 : 0;
        HeadMotion.Y = _pose == "Completed" ? SleepingHeadY : 0;
        LeftHandMotion.Y = _pose is "Running" or "Completed" ? 0 : 1;
        RightHandMotion.Y = _pose is "Running" or "Completed" ? 0 : 1;
        RestingRightArm.Visibility = _pose == "NeedsInput" ? Visibility.Collapsed : Visibility.Visible;
        RightHand.Visibility = _pose == "NeedsInput" ? Visibility.Collapsed : Visibility.Visible;
        RaisedRightArm.Visibility = _pose == "NeedsInput" ? Visibility.Visible : Visibility.Collapsed;
        RaisedHand.Visibility = _pose == "NeedsInput" ? Visibility.Visible : Visibility.Collapsed;
        RaisedHandWave.Angle = 0;
        ScreenCursor.Opacity = _pose == "Running" ? 1 : 0;
        ScreenCode.Opacity = _pose is "Unknown" or "Failed" ? 0.4 : 0.75;
        SleepMotion.Y = 0;
        SleepMarks.Opacity = 0.8;
        Antenna.Opacity = _pose == "Unknown" ? 0.45 : 0.85;
        AutomationProperties.SetName(this, _pose == "NeedsInput" ? "Codex agent needs an answer" : $"Codex agent {_pose.ToLowerInvariant()}");
        AutomationProperties.SetHelpText(this, _pose switch
        {
            "Running" => "The robot is typing while its agent works.",
            "Completed" => "Work completed. The robot is asleep on its keyboard.",
            "Waiting" => "The agent is waiting and is not currently typing.",
            "NeedsInput" => "The chat has an unanswered question. The robot excitedly raises one hand for your answer.",
            "Failed" => "The agent reported a failure. Completion is not confirmed.",
            "Unknown" => "The agent's current status is unavailable.",
            _ => "No active work. The robot is ready."
        });
        RefreshMotion();
    }

    private void RefreshMotion()
    {
        if (!_initialized) return;
        var shouldAnimate = _observing && IsLoaded && IsVisible && Animate
            && SystemParameters.ClientAreaAnimation && (_pose is "Running" or "Completed" or "NeedsInput")
            && _window?.WindowState != WindowState.Minimized && IsInsideViewport();
        if (!shouldAnimate) { StopMotion(); return; }
        if (_animations.Count != 0) return;

        if (_pose == "Running")
        {
            AnimateValue(HeadMotion, TranslateTransform.YProperty, 0, -0.9, 1.35);
            AnimateValue(HeadTilt, RotateTransform.AngleProperty, -1, 1, 1.8);
            AnimateValue(LeftHandMotion, TranslateTransform.YProperty, -1.8, 0.4, 0.22);
            AnimateValue(RightHandMotion, TranslateTransform.YProperty, 0.4, -1.8, 0.22);
            AnimateValue(ScreenCursor, OpacityProperty, 0.35, 1, 0.8);
        }
        else if (_pose == "Completed")
        {
            AnimateValue(HeadMotion, TranslateTransform.YProperty, SleepingHeadY, SleepingHeadY - 0.4, 2.4);
            AnimateValue(SleepMotion, TranslateTransform.YProperty, 0, -2, 2.4);
            AnimateValue(SleepMarks, OpacityProperty, 0.55, 0.9, 2.4);
        }
        else
        {
            AnimateValue(RaisedHandWave, RotateTransform.AngleProperty, -9, 9, 0.32);
            AnimateValue(HeadTilt, RotateTransform.AngleProperty, -3, -1, 0.64);
        }
    }

    private bool IsInsideViewport()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return false;
        // IsVisible does not account for clipping by a scrolled viewport. Layout changes do.
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

    private static Brush FrozenBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
