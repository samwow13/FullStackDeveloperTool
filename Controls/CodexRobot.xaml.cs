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

    public static readonly DependencyProperty IsSpeakingProperty = DependencyProperty.Register(
        nameof(IsSpeaking), typeof(bool), typeof(CodexRobot), new PropertyMetadata(false, AppearanceChanged));

    public static readonly DependencyProperty IsWaitingToSpeakProperty = DependencyProperty.Register(
        nameof(IsWaitingToSpeak), typeof(bool), typeof(CodexRobot), new PropertyMetadata(false, AppearanceChanged));

    public static readonly DependencyProperty IsHoveredProperty = DependencyProperty.Register(
        nameof(IsHovered), typeof(bool), typeof(CodexRobot), new PropertyMetadata(false, AppearanceChanged));

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
    private bool _sleepEntryPending;
    private (double X, double Y, double Tilt) _sleepEntryStart;
    private bool _completedAwake;
    private bool _wakeEntryPending;
    private (double X, double Y, double Tilt) _wakeEntryStart;
    private double _wakeEntryStartSleepOpacity;
    private int _motionVersion;

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

    public bool IsSpeaking
    {
        get => (bool)GetValue(IsSpeakingProperty);
        set => SetValue(IsSpeakingProperty, value);
    }

    public bool IsWaitingToSpeak
    {
        get => (bool)GetValue(IsWaitingToSpeakProperty);
        set => SetValue(IsWaitingToSpeakProperty, value);
    }

    public bool IsHovered
    {
        get => (bool)GetValue(IsHoveredProperty);
        set => SetValue(IsHoveredProperty, value);
    }

    private static void AppearanceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var robot = (CodexRobot)sender;
        if (e.Property == IsHoveredProperty && robot._pose != "Completed") return;
        robot.ApplyAppearance();
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
        _sleepEntryPending = false;
        _wakeEntryPending = false;
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
        var previousPose = _pose;
        var previousHead = (X: HeadMotion.X, Y: HeadMotion.Y, Tilt: HeadTilt.Angle);
        var previousSleepOpacity = SleepMarks.Visibility == Visibility.Visible ? SleepMarks.Opacity : 0;
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
        var queuedMessage = IsWaitingToSpeak && !IsSpeaking;
        var completedAwake = _pose == "Completed" && (IsSpeaking || queuedMessage || IsHovered);
        if (_pose != "Completed")
        {
            _sleepEntryPending = false;
            _wakeEntryPending = false;
        }
        else if (_observing && IsLoaded)
        {
            if (completedAwake)
            {
                // Hover, queued-message and popup bindings may arrive in either order. Preserve an in-flight wake.
                if (previousPose == "Completed" && !_completedAwake) _wakeEntryPending = true;
                _sleepEntryPending = false;
            }
            else
            {
                if (previousPose != "Completed" || _completedAwake) _sleepEntryPending = true;
                _wakeEntryPending = false;
            }
        }
        _completedAwake = completedAwake;
        var awake = IsSpeaking || queuedMessage || completedAwake;
        // Interrupted transitions restart from the effective head position, never from a snapped pose.
        if (_sleepEntryPending) _sleepEntryStart = previousHead;
        if (_wakeEntryPending)
        {
            _wakeEntryStart = previousHead;
            _wakeEntryStartSleepOpacity = previousSleepOpacity;
        }

        Foreground = _pose switch
        {
            "Running" => RunningBrush,
            "Completed" => CompletedBrush,
            "Waiting" or "NeedsInput" => WaitingBrush,
            "Failed" => FailedBrush,
            "Idle" => IdleBrush,
            _ => UnknownBrush
        };
        OpenEyes.Visibility = (awake && _pose != "NeedsInput") || _pose is "Running" or "Idle" ? Visibility.Visible : Visibility.Collapsed;
        SleepEyes.Visibility = !awake && _pose == "Completed" ? Visibility.Visible : Visibility.Collapsed;
        WaitingEyes.Visibility = !awake && _pose == "Waiting" ? Visibility.Visible : Visibility.Collapsed;
        ExcitedEyes.Visibility = _pose == "NeedsInput" ? Visibility.Visible : Visibility.Collapsed;
        FailedEyes.Visibility = !awake && _pose == "Failed" ? Visibility.Visible : Visibility.Collapsed;
        UnknownEyes.Visibility = !awake && _pose == "Unknown" ? Visibility.Visible : Visibility.Collapsed;
        SleepMarks.Visibility = _pose == "Completed" && (!awake || _wakeEntryPending) ? Visibility.Visible : Visibility.Collapsed;
        RestingMouth.Visibility = IsSpeaking ? Visibility.Collapsed : Visibility.Visible;
        RestingMouth.Data = Geometry.Parse(_pose switch
        {
            "Completed" => "M 29,25 L 31,25",
            "NeedsInput" => "M 27,23 Q 30,29 33,23 Z",
            "Failed" => "M 28,25 Q 30,23 32,25",
            "Waiting" or "Unknown" => "M 29,25 L 32,25",
            _ => "M 28,24 Q 30,25.5 32,24"
        });
        TalkingMouth.Visibility = IsSpeaking ? Visibility.Visible : Visibility.Collapsed;
        MouthOpening.ScaleY = 1;
        StateMark.Visibility = _pose is "Waiting" or "NeedsInput" or "Unknown" or "Failed" ? Visibility.Visible : Visibility.Collapsed;
        StateMark.Text = _pose switch { "Waiting" => "…", "Failed" => "!", _ => "?" };

        // The lowered head's bottom edge meets the top keyboard row, even with motion disabled.
        HeadTilt.Angle = awake ? 0 : _pose switch { "Completed" => SleepingHeadTilt, "NeedsInput" => -3, "Waiting" => -5, "Unknown" => -7, _ => 0 };
        HeadMotion.X = !awake && _pose == "Completed" ? 1 : 0;
        HeadMotion.Y = !awake && _pose == "Completed" ? SleepingHeadY : 0;
        LeftHandMotion.Y = _pose is "Running" or "Completed" ? 0 : 1;
        RightHandMotion.Y = _pose is "Running" or "Completed" ? 0 : 1;
        var needsAnswerPose = _pose == "NeedsInput" && !IsSpeaking && !queuedMessage;
        RestingRightArm.Visibility = needsAnswerPose || queuedMessage ? Visibility.Collapsed : Visibility.Visible;
        RightHand.Visibility = needsAnswerPose || queuedMessage ? Visibility.Collapsed : Visibility.Visible;
        RaisedRightArm.Visibility = needsAnswerPose ? Visibility.Visible : Visibility.Collapsed;
        RaisedHand.Visibility = needsAnswerPose ? Visibility.Visible : Visibility.Collapsed;
        RaisedHandWave.Angle = 0;
        QueuedMessageArm.Visibility = queuedMessage ? Visibility.Visible : Visibility.Collapsed;
        QueuedMessageWave.Angle = 10;
        ScreenCursor.Opacity = _pose == "Running" ? 1 : 0;
        ScreenCode.Opacity = _pose is "Unknown" or "Failed" ? 0.4 : 0.75;
        SleepMotion.Y = 0;
        SleepMarks.Opacity = completedAwake ? 0 : 0.8;
        Antenna.Opacity = _pose == "Unknown" ? 0.45 : 0.85;
        AutomationProperties.SetName(this, queuedMessage ? "Codex agent has an unread message waiting to speak" :
            _pose == "NeedsInput" ? "Codex agent needs an answer" : $"Codex agent {_pose.ToLowerInvariant()}");
        AutomationProperties.SetHelpText(this, queuedMessage ?
            (_pose == "NeedsInput" ? "The agent has an unanswered question and waves until its unread message gets a chat bubble." :
                "The agent waves its raised hand until its unread message gets a turn in the chat bubble.") : _pose switch
        {
            "Running" => IsSpeaking ? "The agent is working and showing a message in its chat bubble." : "The robot is typing while its agent works.",
            "Completed" => IsSpeaking ? "Work completed. The robot is showing its saved message in a chat bubble." :
                completedAwake ? "Work completed. The robot is awake while you hover over it." : "Work completed. The robot is asleep on its keyboard.",
            "Waiting" => "The agent is waiting and is not currently typing.",
            "NeedsInput" => IsSpeaking ? "The chat has an unanswered question. The robot is showing it in its chat bubble." :
                "The chat has an unanswered question. The robot excitedly raises one hand for your answer.",
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
            && SystemParameters.ClientAreaAnimation && (IsSpeaking || IsWaitingToSpeak || _pose is "Running" or "Completed" or "NeedsInput")
            && _window?.WindowState != WindowState.Minimized && IsInsideViewport();
        if (!shouldAnimate) { _sleepEntryPending = false; _wakeEntryPending = false; StopMotion(); return; }
        if (_animations.Count != 0) return;

        if (_wakeEntryPending && _completedAwake)
        {
            if (IsSpeaking) AnimateMouth();
            else if (IsWaitingToSpeak) AnimateQueuedMessageWave();
            AnimateWakeEntry();
            return;
        }
        if (IsSpeaking)
        {
            AnimateMouth();
            AnimateValue(HeadMotion, TranslateTransform.YProperty, 0, -1.2, 0.7);
            AnimateValue(HeadTilt, RotateTransform.AngleProperty, -2, 2, 0.9);
        }
        else if (IsWaitingToSpeak)
        {
            AnimateQueuedMessageWave();
            AnimateValue(HeadMotion, TranslateTransform.YProperty, 0, -1.4, 0.76);
            AnimateValue(HeadTilt, RotateTransform.AngleProperty, -3, 2, 0.76);
        }
        else if (_pose == "Running")
        {
            AnimateValue(HeadMotion, TranslateTransform.YProperty, 0, -0.9, 1.35);
            AnimateValue(HeadTilt, RotateTransform.AngleProperty, -1, 1, 1.8);
            AnimateValue(LeftHandMotion, TranslateTransform.YProperty, -1.8, 0.4, 0.22);
            AnimateValue(RightHandMotion, TranslateTransform.YProperty, 0.4, -1.8, 0.22);
            AnimateValue(ScreenCursor, OpacityProperty, 0.35, 1, 0.8);
        }
        else if (_pose == "Completed")
        {
            if (_completedAwake) return;
            if (_sleepEntryPending)
            {
                AnimateSleepEntry();
                return;
            }
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

    private void AnimateQueuedMessageWave() =>
        AnimateValue(QueuedMessageWave, RotateTransform.AngleProperty, -5, 25, 0.38);

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

    private void AnimateMouth()
    {
        // Hold both fully open and closed shapes so speech reads clearly at the smaller subagent size.
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(1.24),
            RepeatBehavior = RepeatBehavior.Forever
        };
        foreach (var (seconds, opening) in new (double, double)[]
        {
            (0, 0.14), (0.12, 1), (0.24, 1), (0.32, 0.14),
            (0.44, 0.72), (0.54, 0.72), (0.64, 0.14),
            (0.78, 1), (0.90, 1), (1.02, 0.14), (1.24, 0.14)
        })
            animation.KeyFrames.Add(new SplineDoubleKeyFrame(opening,
                KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds)), new KeySpline(0.3, 0, 0.3, 1)));
        Timeline.SetDesiredFrameRate(animation, 24);
        MouthOpening.BeginAnimation(ScaleTransform.ScaleYProperty, animation, HandoffBehavior.SnapshotAndReplace);
        _animations.Add((MouthOpening, ScaleTransform.ScaleYProperty));
    }

    private void AnimateSleepEntry()
    {
        var version = _motionVersion;
        // Wind up, hit the keyboard, rebound once, then keep the existing resting pose.
        AnimateOnce(HeadTilt, RotateTransform.AngleProperty,
            [(0, _sleepEntryStart.Tilt), (0.16, -6), (0.30, 12), (0.41, 5), (0.68, SleepingHeadTilt)]);
        AnimateOnce(HeadMotion, TranslateTransform.XProperty,
            [(0, _sleepEntryStart.X), (0.16, 0), (0.30, 1.6), (0.41, 0.8), (0.68, 1)]);
        AnimateOnce(SleepMarks, OpacityProperty,
            [(0, 0), (0.44, 0), (0.68, 0.8)]);
        AnimateOnce(HeadMotion, TranslateTransform.YProperty,
            [(0, _sleepEntryStart.Y), (0.16, -2), (0.30, SleepingHeadY + 1),
             (0.41, SleepingHeadY - 4), (0.68, SleepingHeadY)], () =>
            {
                // Removed clocks may still have queued completion notifications.
                if (version != _motionVersion || !_sleepEntryPending || _pose != "Completed" || _completedAwake) return;
                _sleepEntryPending = false;
                StopMotion();
                RefreshMotion();
            });
    }

    private void AnimateWakeEntry()
    {
        var version = _motionVersion;
        // Reverse the sleep keyframes: rebound off the keyboard, lift the head, then settle upright.
        AnimateOnce(HeadTilt, RotateTransform.AngleProperty,
            [(0, _wakeEntryStart.Tilt), (0.27, 5), (0.38, 12), (0.52, -6), (0.68, 0)], reverse: true);
        AnimateOnce(HeadMotion, TranslateTransform.XProperty,
            [(0, _wakeEntryStart.X), (0.27, 0.8), (0.38, 1.6), (0.52, 0), (0.68, 0)], reverse: true);
        AnimateOnce(SleepMarks, OpacityProperty,
            [(0, _wakeEntryStartSleepOpacity), (0.24, 0), (0.68, 0)], reverse: true);
        AnimateOnce(HeadMotion, TranslateTransform.YProperty,
            [(0, _wakeEntryStart.Y), (0.27, SleepingHeadY - 4), (0.38, SleepingHeadY + 1),
             (0.52, -2), (0.68, 0)], () =>
            {
                if (version != _motionVersion || !_wakeEntryPending || _pose != "Completed" || !_completedAwake) return;
                _wakeEntryPending = false;
                StopMotion();
                RefreshMotion();
            }, reverse: true);
    }

    private void AnimateOnce(IAnimatable target, DependencyProperty property,
        (double Seconds, double Value)[] frames, Action? completed = null, bool reverse = false)
    {
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(0.68),
            FillBehavior = FillBehavior.HoldEnd
        };
        foreach (var (seconds, value) in frames)
            animation.KeyFrames.Add(new SplineDoubleKeyFrame(value,
                KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds)),
                reverse ? new KeySpline(0.7, 0, 0.7, 1) : new KeySpline(0.3, 0, 0.3, 1)));
        if (completed != null) animation.Completed += (_, _) => completed();
        Timeline.SetDesiredFrameRate(animation, 24);
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        _animations.Add((target, property));
    }

    private void StopMotion()
    {
        _motionVersion++;
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
