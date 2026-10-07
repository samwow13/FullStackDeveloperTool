using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace FullStackLauncher.Controls;

/// <summary>A brief, passive celebration when a new commit message arrives.</summary>
public partial class CommitHammerRobot : UserControl
{
    private const double TimelineSeconds = 3.6;
    private const double TimingScale = 2;
    private const double AnimationSeconds = TimelineSeconds * TimingScale;
    private readonly List<(IAnimatable Target, DependencyProperty Property)> _animations = [];
    private readonly List<Brush> _textRevealBrushes = [];
    private readonly DispatcherTimer _dismissTimer;
    private Window? _window;
    private bool _observing;
    private bool _active;

    public CommitHammerRobot()
    {
        InitializeComponent();
        _dismissTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
        _dismissTimer.Tick += DismissTimer_Tick;
        Loaded += Robot_Loaded;
        Unloaded += Robot_Unloaded;
        IsVisibleChanged += Robot_IsVisibleChanged;
    }

    public void Play(IReadOnlyList<Brush>? textRevealBrushes = null)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(() => Play(textRevealBrushes)));
            return;
        }

        Stop();
        if (!CanShow()) return;
        _active = true;
        Artwork.Opacity = 1;
        _dismissTimer.Interval = TimeSpan.FromSeconds(SystemParameters.ClientAreaAnimation ? AnimationSeconds : 3.4);
        _dismissTimer.Start();
        if (!SystemParameters.ClientAreaAnimation)
        {
            HammerSwing.Angle = 0;
            HeadTilt.Angle = 3;
            return;
        }

        Animate(Artwork, OpacityProperty,
            [(0, 0), (0.18, 1), (3.08, 1), (TimelineSeconds, 0)]);
        Animate(HammerSwing, RotateTransform.AngleProperty,
            [(0, -25), (0.33, -63), (0.60, 0), (0.69, -17),
             (1.12, -63), (1.39, 0), (1.48, -17),
             (1.91, -63), (2.18, 0), (2.27, -17), (2.65, -25), (TimelineSeconds, -25)]);
        Animate(HeadTilt, RotateTransform.AngleProperty,
            [(0, -2), (0.33, -6), (0.60, 4), (0.78, 0),
             (1.12, -6), (1.39, 4), (1.57, 0),
             (1.91, -6), (2.18, 4), (2.36, 0), (2.65, -2), (TimelineSeconds, -2)]);
        Animate(BodyMotion, TranslateTransform.YProperty,
            [(0, 0), (0.33, -1), (0.60, 1.5), (0.77, 0),
             (1.12, -1), (1.39, 1.5), (1.56, 0),
             (1.91, -1), (2.18, 1.5), (2.35, 0), (TimelineSeconds, 0)]);
        Animate(Sparks, OpacityProperty,
            [(0, 0), (0.59, 0), (0.60, 1), (0.82, 0),
             (1.38, 0), (1.39, 1), (1.61, 0),
             (2.17, 0), (2.18, 1), (2.40, 0), (TimelineSeconds, 0)], linear: true);
        var expansion = new (double Seconds, double Value)[]
        {
            (0, 0.65), (0.59, 0.65), (0.60, 0.65), (0.82, 1.15),
            (1.38, 0.65), (1.39, 0.65), (1.61, 1.15),
            (2.17, 0.65), (2.18, 0.65), (2.40, 1.15), (TimelineSeconds, 1.15)
        };
        Animate(SparkExpansion, ScaleTransform.ScaleXProperty, expansion, linear: true);
        Animate(SparkExpansion, ScaleTransform.ScaleYProperty, expansion, linear: true);
        if (textRevealBrushes is not { Count: > 0 }) return;
        double[] impacts = [0.60, 1.39, 2.18];
        for (var index = 0; index < textRevealBrushes.Count; index++)
        {
            var progress = index * 3.0 / textRevealBrushes.Count;
            var swing = Math.Min(2, (int)progress);
            var start = impacts[swing] + (progress - swing) * 0.5;
            var brush = textRevealBrushes[index];
            _textRevealBrushes.Add(brush);
            Animate(brush, Brush.OpacityProperty,
                [(0, 0), (start, 0), (start + 0.12, 1), (TimelineSeconds, 1)], linear: true);
        }
    }

    public void CompleteTextReveal()
    {
        foreach (var brush in _textRevealBrushes)
        {
            brush.BeginAnimation(Brush.OpacityProperty, null);
            _animations.Remove((brush, Brush.OpacityProperty));
        }
        _textRevealBrushes.Clear();
    }

    public void Stop()
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(Stop));
            return;
        }

        _active = false;
        _dismissTimer.Stop();
        CompleteTextReveal();
        foreach (var (target, property) in _animations) target.BeginAnimation(property, null);
        _animations.Clear();
        Artwork.Opacity = 0;
        Sparks.Opacity = 0;
        HammerSwing.Angle = -25;
        HeadTilt.Angle = 0;
        BodyMotion.Y = 0;
        SparkExpansion.ScaleX = 0.7;
        SparkExpansion.ScaleY = 0.7;
    }

    private void Robot_Loaded(object sender, RoutedEventArgs e)
    {
        if (_observing) return;
        _observing = true;
        SystemParameters.StaticPropertyChanged += MotionPreferenceChanged;
        LayoutUpdated += Robot_LayoutUpdated;
        _window = Window.GetWindow(this);
        if (_window != null) _window.StateChanged += Window_StateChanged;
    }

    private void Robot_Unloaded(object sender, RoutedEventArgs e)
    {
        Stop();
        if (!_observing) return;
        _observing = false;
        SystemParameters.StaticPropertyChanged -= MotionPreferenceChanged;
        LayoutUpdated -= Robot_LayoutUpdated;
        if (_window != null) _window.StateChanged -= Window_StateChanged;
        _window = null;
    }

    private void DismissTimer_Tick(object? sender, EventArgs e) => Stop();

    private void Robot_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_active && !CanShow()) Stop();
    }

    private void Robot_LayoutUpdated(object? sender, EventArgs e)
    {
        if (_active && !CanShow()) Stop();
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (_active && !CanShow()) Stop();
    }

    private void MotionPreferenceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (null or "" or nameof(SystemParameters.ClientAreaAnimation))) return;
        if (Dispatcher.CheckAccess())
        {
            if (_active && !SystemParameters.ClientAreaAnimation) Stop();
        }
        else if (!Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_observing && _active && !SystemParameters.ClientAreaAnimation) Stop();
            }));
    }

    private bool CanShow()
    {
        if (!_observing || !IsLoaded || !IsVisible || _window?.WindowState == WindowState.Minimized
            || ActualWidth <= 0 || ActualHeight <= 0) return false;

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

    private void Animate(IAnimatable target, DependencyProperty property,
        (double Seconds, double Value)[] frames, bool linear = false)
    {
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(AnimationSeconds),
            FillBehavior = FillBehavior.HoldEnd
        };
        foreach (var (seconds, value) in frames)
        {
            var keyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds * TimingScale));
            animation.KeyFrames.Add(linear
                ? new LinearDoubleKeyFrame(value, keyTime)
                : new SplineDoubleKeyFrame(value, keyTime, new KeySpline(0.3, 0, 0.3, 1)));
        }
        Timeline.SetDesiredFrameRate(animation, 30);
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        _animations.Add((target, property));
    }
}
