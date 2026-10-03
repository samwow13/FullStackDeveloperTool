using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FullStackLauncher.Services;
using FullStackLauncher.Models;

namespace FullStackLauncher;

public partial class GitWorkspaceWindow
{
    private bool _publishFeedbackRunning;
    private bool _publishFeedbackClosed;
    private bool _publishFeedbackLifetimeAttached;
    private bool _publishMotionPreferenceAttached;
    private string? _publishedLink;
    private string? _postPushLinkError;
    private readonly Dictionary<string, string> _postPushLinkDrafts = new(StringComparer.Ordinal);

    private async void PostPushLinkConfig_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || Root is not { } root || ConnectionPicker.SelectedItem is not GitRemoteInfo remote)
        {
            SetStatus("Select a Git connection before customizing its after-push link.");
            return;
        }
        var scope = root + "\0" + AgentGitChangeStore.ConnectionId(remote);
        _postPushLinkDrafts.TryGetValue(scope, out var draft);
        var dialog = new GitPostPushLinkWindow(root, remote, draft) { Owner = this };
        var accepted = dialog.ShowDialog() == true;
        _postPushLinkDrafts[scope] = dialog.Draft;
        if (!accepted) return;
        if (await ExecuteAsync("Save after-push link", token => GitRepositoryService.SavePostPushUrlAsync(root, remote.Name, dialog.Link, token),
            useResultAsStatus: true)) _postPushLinkDrafts.Remove(scope);
    }

    private async Task OpenPostPushLinkAsync(GitRemoteInfo remote)
    {
        if (remote.PostPushLinkError is { } error)
        {
            _postPushLinkError = error;
            PublishFeedbackDetail.Text += " " + error;
            AddActivity("After-push link", remote.Name, error);
            return;
        }
        _publishedLink = PostPushLink.Url(remote);
        if (_publishedLink.Length == 0) return;
        PublishLinkText.Text = _publishedLink;
        var uri = new Uri(_publishedLink, UriKind.Absolute);
        PublishLinkOpen.Content = uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase) ? "Open Azure" : "Open link";
        PublishLinkPanel.Visibility = Visibility.Visible;
        await OpenPublishedLinkAsync();
    }

    private async void PublishLinkOpen_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await OpenPublishedLinkAsync();
    }

    private void PublishLinkCopy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_publishedLink)) return;
        try
        {
            Clipboard.SetText(_publishedLink);
            PublishLinkStatus.Text = "Link copied.";
        }
        catch (System.Runtime.InteropServices.ExternalException exception)
        {
            PublishLinkStatus.Text = "The link could not be copied. Try Copy link again.";
            AddActivity("Copy after-push link", _publishedLink, SafeError(exception));
        }
    }

    private bool _openingPublishedLink;
    private async Task OpenPublishedLinkAsync()
    {
        if (_openingPublishedLink || string.IsNullOrEmpty(_publishedLink)) return;
        var link = _publishedLink;
        _openingPublishedLink = true;
        PublishLinkStatus.Text = "Opening Chrome…";
        try
        {
            await Task.Run(() => ChromeLinkLauncher.Open(link));
            if (_publishedLink == link) PublishLinkStatus.Text = "Link sent to Chrome.";
        }
        catch (Exception exception)
        {
            var detail = "Push succeeded. " + SafeError(exception);
            if (_publishedLink == link) PublishLinkStatus.Text = detail;
            AddActivity("Open after-push link", link, detail);
        }
        finally { _openingPublishedLink = false; }
    }

    private void BeginPublishFeedback(string branch, string remote)
    {
        if (!Dispatcher.CheckAccess())
        {
            DispatchPublishFeedback(() => BeginPublishFeedback(branch, remote));
            return;
        }
        if (_publishFeedbackClosed) return;
        if (!_publishFeedbackLifetimeAttached)
        {
            Closed += PublishFeedback_Closed;
            _publishFeedbackLifetimeAttached = true;
        }

        StopPublishAnimations();
        _publishedLink = null;
        _postPushLinkError = null;
        PublishLinkPanel.Visibility = Visibility.Collapsed;
        PublishLinkStatus.Text = "";
        _publishFeedbackRunning = true;
        PublishFeedbackCard.Background = PublishBrush(0x10, 0x29, 0x23);
        PublishFeedbackCard.BorderBrush = PublishBrush(0x38, 0x6C, 0x5E);
        PublishFeedbackHeading.Foreground = PublishBrush(0xBB, 0xFC, 0xE5);
        PublishFeedbackHeading.Text = "Preparing your commit";
        PublishFeedbackDestination.Text = SensitiveDataProtection.Redact($"Destination: {remote}/{branch}");
        PublishFeedbackDetail.Text = "Checking the destination and preparing your local changes.";
        PublishWorkingIcon.Visibility = Visibility.Visible;
        PublishSuccessIcon.Visibility = Visibility.Collapsed;
        PublishUnconfirmedIcon.Visibility = Visibility.Collapsed;
        PublishFeedbackDismiss.Visibility = Visibility.Collapsed;
        PublishFeedbackCard.Visibility = Visibility.Visible;
        StartPublishProgressAnimation();
        AnnouncePublishFeedback();
        // Keep the feedback in view without changing keyboard focus.
        DispatchPublishFeedback(() =>
        {
            if (_publishFeedbackRunning && PublishFeedbackCard.IsVisible)
                PublishFeedbackCard.BringIntoView();
        });
    }

    private void SetPublishPhase(string phase)
    {
        if (!Dispatcher.CheckAccess())
        {
            DispatchPublishFeedback(() => SetPublishPhase(phase));
            return;
        }
        if (!_publishFeedbackRunning || _publishFeedbackClosed || string.IsNullOrWhiteSpace(phase)) return;
        var display = SensitiveDataProtection.Redact(phase);
        if (PublishFeedbackHeading.Text == display) return;
        PublishFeedbackHeading.Text = display;
        PublishFeedbackDetail.Text = "Working on the selected destination.";
        AnnouncePublishFeedback();
    }

    private void CompletePublishFeedback(bool confirmed, string detail)
    {
        if (!Dispatcher.CheckAccess())
        {
            DispatchPublishFeedback(() => CompletePublishFeedback(confirmed, detail));
            return;
        }
        if (_publishFeedbackClosed || (!_publishFeedbackRunning && PublishFeedbackCard.Visibility != Visibility.Visible)) return;
        var firstCompletion = _publishFeedbackRunning;
        _publishFeedbackRunning = false;
        StopPublishAnimations();
        PublishWorkingIcon.Visibility = Visibility.Collapsed;
        PublishSuccessIcon.Visibility = confirmed ? Visibility.Visible : Visibility.Collapsed;
        PublishUnconfirmedIcon.Visibility = confirmed ? Visibility.Collapsed : Visibility.Visible;
        PublishFeedbackDismiss.Visibility = Visibility.Visible;
        PublishFeedbackHeading.Text = confirmed ? "Pushed successfully" : "Push not confirmed";
        PublishFeedbackDetail.Text = string.IsNullOrWhiteSpace(detail)
            ? confirmed ? "Your commits reached the selected remote." : "Review Activity for details before trying again."
            : SensitiveDataProtection.Redact(detail);
        if (_postPushLinkError != null) PublishFeedbackDetail.Text += " " + _postPushLinkError;
        if (confirmed)
        {
            PublishFeedbackCard.Background = PublishBrush(0x12, 0x30, 0x26);
            PublishFeedbackCard.BorderBrush = PublishBrush(0x62, 0xB9, 0x98);
            if (firstCompletion && PublishFeedbackCard.IsVisible && SystemParameters.ClientAreaAnimation)
                AnimatePublishSuccess();
        }
        else
        {
            PublishFeedbackCard.Background = PublishBrush(0x30, 0x29, 0x1F);
            PublishFeedbackCard.BorderBrush = PublishBrush(0xA0, 0x78, 0x50);
            PublishFeedbackHeading.Foreground = PublishBrush(0xFF, 0xE0, 0xBC);
        }
        AnnouncePublishFeedback();
    }

    private void ResetPublishFeedback()
    {
        if (!Dispatcher.CheckAccess())
        {
            DispatchPublishFeedback(ResetPublishFeedback);
            return;
        }
        _publishFeedbackRunning = false;
        StopPublishAnimations();
        PublishFeedbackCard.Visibility = Visibility.Collapsed;
        PublishFeedbackHeading.Text = "";
        PublishFeedbackDestination.Text = "";
        PublishFeedbackDetail.Text = "";
        _publishedLink = null;
        _postPushLinkError = null;
        PublishLinkText.Text = "";
        PublishLinkPanel.Visibility = Visibility.Collapsed;
        PublishLinkStatus.Text = "";
    }

    private void StartPublishProgressAnimation()
    {
        if (!_publishFeedbackRunning || _publishFeedbackClosed || !PublishFeedbackCard.IsVisible) return;
        ObservePublishMotionPreference();
        if (!SystemParameters.ClientAreaAnimation) return;
        PublishSpinnerRotate.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(1100))
            {
                RepeatBehavior = RepeatBehavior.Forever
            });
    }

    private void AnimatePublishSuccess()
    {
        ObservePublishMotionPreference();
        var settle = new DoubleAnimation(0.78, 1, TimeSpan.FromMilliseconds(420))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.22 },
            FillBehavior = FillBehavior.Stop
        };
        PublishSuccessScale.BeginAnimation(ScaleTransform.ScaleXProperty, settle);
        PublishSuccessScale.BeginAnimation(ScaleTransform.ScaleYProperty, settle);
        var spread = new DoubleAnimation(0.74, 1.28, TimeSpan.FromMilliseconds(650))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        PublishBurstScale.BeginAnimation(ScaleTransform.ScaleXProperty, spread);
        PublishBurstScale.BeginAnimation(ScaleTransform.ScaleYProperty, spread);
        var fade = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(650))));
        fade.Completed += (_, _) => StopPublishAnimations();
        PublishBurst.BeginAnimation(OpacityProperty, fade);
    }

    private void StopPublishAnimations()
    {
        PublishSpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        PublishSuccessScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PublishSuccessScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        PublishBurstScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PublishBurstScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        PublishBurst.BeginAnimation(OpacityProperty, null);
        if (_publishMotionPreferenceAttached)
        {
            SystemParameters.StaticPropertyChanged -= PublishMotionPreference_Changed;
            _publishMotionPreferenceAttached = false;
        }
    }

    private void ObservePublishMotionPreference()
    {
        if (_publishMotionPreferenceAttached) return;
        SystemParameters.StaticPropertyChanged += PublishMotionPreference_Changed;
        _publishMotionPreferenceAttached = true;
    }

    private void PublishMotionPreference_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SystemParameters.ClientAreaAnimation)) return;
        DispatchPublishFeedback(() =>
        {
            StopPublishAnimations();
            StartPublishProgressAnimation();
        });
    }

    private void PublishFeedback_Loaded(object sender, RoutedEventArgs e) => StartPublishProgressAnimation();
    private void PublishFeedback_Unloaded(object sender, RoutedEventArgs e) => StopPublishAnimations();

    private void PublishFeedback_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue) StartPublishProgressAnimation();
        else StopPublishAnimations();
    }

    private void PublishFeedback_Closed(object? sender, EventArgs e)
    {
        _publishFeedbackClosed = true;
        _publishFeedbackRunning = false;
        StopPublishAnimations();
    }

    private void PublishFeedbackDismiss_Click(object sender, RoutedEventArgs e)
    {
        if (!_publishFeedbackRunning) ResetPublishFeedback();
    }

    private void DispatchPublishFeedback(Action action)
    {
        if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            Dispatcher.BeginInvoke(action);
    }

    private void AnnouncePublishFeedback()
    {
        if (!AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged)) return;
        var peer = UIElementAutomationPeer.FromElement(PublishFeedbackHeading)
            ?? UIElementAutomationPeer.CreatePeerForElement(PublishFeedbackHeading);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private static SolidColorBrush PublishBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
