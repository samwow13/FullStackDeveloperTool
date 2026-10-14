using System.Runtime.InteropServices;
using System.Windows;
using FullStackLauncher.ProjectTasks;
using FullStackLauncher.Services;

namespace FullStackLauncher.Controls;

public partial class CodexStartAgentWindow
{
    private CancellationTokenSource? _contextGathererLifetime;
    private bool _changingContextGatherer;
    private bool _receivingContextGathererDraft;
    private DesktopCaptureResumeTarget? _lastCaptureSource;
    private DesktopContextGathererResumeState? _dashboardResumeGatherer;

    private void UpdateContextGathererControls()
    {
        ContextGathererButton.IsEnabled = !IsSubmitting && !_checkingStatus && !_savingContext && !_pastingImage &&
            !_closingContext && !_closed && PendingAttemptId is null && _composerDraft is null &&
            !_changingContextGatherer && !_receivingContextGathererDraft && !IsDictating;
        ContextGathererButton.Content = _changingContextGatherer ? "Gathering…"
            : _dashboardResumeGatherer is not null ? "Resume Context _Gatherer" : "Context _Gatherer";
        ContextGathererStatus.Text = _changingContextGatherer ? "Capture, review, then write your prompt" : "Any app · Ctrl+drag to capture";
        ContextGathererStatus.Visibility = _changingContextGatherer ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ContextGatherer_Click(object sender, RoutedEventArgs e) => await OpenContextGathererAsync();

    private async Task OpenContextGathererAsync()
    {
        if (_closed || _closingContext || _lifetime.IsCancellationRequested || !ContextGathererButton.IsEnabled) return;
        SettingsPopup.IsOpen = false;
        var existing = DraftImages;
        var remainingCount = CodexAgentChatImage.MaximumCount - existing.Count;
        if (remainingCount <= 0)
        {
            ShowStatus("Attach at most 12 images. Remove an image before gathering another.", error: true);
            return;
        }
        _changingContextGatherer = true;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _contextGathererLifetime = lifetime;
        UpdateControls();
        // Keep ShowDialog and its native modal protection intact. Capture views
        // only change visibility, never enable the dashboard behind this draft.
        var windows = Application.Current.Windows.Cast<Window>().Where(window => window.IsVisible)
            .OrderBy(WindowOwnerDepth)
            .Select(window => (Window: window, State: window.WindowState)).ToArray();
        var wasEnabled = IsEnabled;
        DesktopContextGathererResult? gathered = null;
        var sourceTransferred = false;
        var savedForReplacement = false;
        var gatherCompletedNormally = false;
        void CheckpointForReplacement(DesktopContextGathererResumeState state)
        {
            var previous = _dashboardResumeGatherer;
            _dashboardResumeGatherer = state;
            try
            {
                if (_saveDashboardResume is null)
                    throw new InvalidOperationException("Context Gatherer resume storage is unavailable. Captures are kept.");
                _saveDashboardResume();
                savedForReplacement = true;
            }
            catch
            {
                _dashboardResumeGatherer = previous;
                throw;
            }
        }
        void ChangeCaptureView(DesktopCaptureView view)
        {
            var openWindows = Application.Current.Windows.Cast<Window>().ToHashSet();
            bool ShowWindow(Window window) => view != DesktopCaptureView.OtherApps &&
                (view == DesktopCaptureView.Prompt || !ReferenceEquals(window, this));
            // Restore ancestors first, then minimize excluded children again:
            // restoring an owner can also restore its owned composer.
            foreach (var (window, state) in windows)
                if (openWindows.Contains(window) && ShowWindow(window)) window.WindowState = state;
            foreach (var (window, _) in windows.Reverse())
                if (openWindows.Contains(window) && !ShowWindow(window)) window.WindowState = WindowState.Minimized;
            var target = view == DesktopCaptureView.Prompt ? this : view == DesktopCaptureView.Launcher ? Owner : null;
            if (target is not null && openWindows.Contains(target))
            {
                if (target.WindowState == WindowState.Minimized) target.WindowState = WindowState.Normal;
                var handle = new System.Windows.Interop.WindowInteropHelper(target).Handle;
                // Raise the view without activating a modal-disabled window.
                if (handle == 0 || !SetWindowPos(handle, 0, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0200))
                    throw new InvalidOperationException("The selected Launcher window could not be shown. Choose the capture view again.");
            }
        }
        try
        {
            var remainingBytes = await Task.Run(() =>
            {
                CodexAgentImageStaging.ValidateImages(existing);
                return CodexAgentChatImage.MaximumTotalBytes - existing.Sum(image => Convert.FromBase64String(image.DataBase64).Length);
            }, lifetime.Token);
            if (remainingBytes <= 0)
            {
                ShowStatus("Attached images already use the 24 MB limit. Remove an image before gathering another.", error: true);
                return;
            }
            IsEnabled = false;
            var rememberedSource = _lastCaptureSource;
            var initialView = _dashboardResumeGatherer?.CaptureView ??
                (rememberedSource is { IsLauncherWindow: true } && rememberedSource.IsCurrent()
                ? rememberedSource.Handle == new System.Windows.Interop.WindowInteropHelper(this).Handle
                    ? DesktopCaptureView.Prompt : DesktopCaptureView.Launcher
                : DesktopCaptureView.OtherApps);
            ChangeCaptureView(initialView);
            gathered = await DesktopContextGathererWindow.GatherAsync(this, remainingCount, remainingBytes,
                ChangeCaptureView, initialView, _dashboardResumeGatherer is not null || rememberedSource is null
                    ? null : rememberedSource.TryRestoreAsync, lifetime.Token,
                _dashboardResumeGatherer, CheckpointForReplacement);
            if (_closed || lifetime.IsCancellationRequested) return;
            if (gathered is null || gathered.Images.Count == 0)
            {
                gatherCompletedNormally = true;
                return;
            }
            var captured = gathered.Images;
            // Retain the completed gallery until every preview transfers into the composer.
            // A reconstruction failure must leave those screenshots available for retry.
            _dashboardResumeGatherer = new DesktopContextGathererResumeState
            {
                ConfirmedImages = captured,
                CaptureView = initialView,
                CaptureEnabled = false,
                NextCaption = captured.Count + 1
            };
            _receivingContextGathererDraft = true;
            var prepared = await Task.Run(() =>
            {
                CodexAgentImageStaging.ValidateImages(existing.Concat(captured).ToArray());
                return captured.Select(image => new MessageImage(image, MakePreview(image))).ToArray();
            });
            if (_closed) return;
            foreach (var image in prepared) _messageImages.Add(image);
            _lastCaptureSource?.Dispose();
            _lastCaptureSource = gathered.ResumeTarget;
            sourceTransferred = true;
            gatherCompletedNormally = true;
            UpdateMessageImages();
            ShowStatus($"{prepared.Length} screenshot{(prepared.Length == 1 ? "" : "s")} added. Write your prompt, then choose Wait & next or Start Agent.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_closed) ShowStatus("Context Gatherer could not finish. Your existing message and images are kept. " +
                SensitiveDataProtection.Redact(ex.Message), error: true);
        }
        finally
        {
            _contextGathererLifetime = null;
            if (!sourceTransferred) gathered?.ResumeTarget?.Dispose();
            // Restore ancestors before their owned windows, without reopening closed windows.
            var open = Application.Current.Windows.Cast<Window>().ToHashSet();
            var restoreFailed = false;
            foreach (var (window, state) in windows)
            {
                if (!open.Contains(window)) continue;
                try { window.WindowState = state; }
                catch (InvalidOperationException) { restoreFailed = true; }
            }
            _changingContextGatherer = false;
            _receivingContextGathererDraft = false;
            if (!savedForReplacement && gatherCompletedNormally)
            {
                _dashboardResumeGatherer = null;
                try { _saveDashboardResume?.Invoke(); }
                catch (Exception)
                {
                    if (!_closed) ShowStatus("Current draft could not be autosaved. Your message and captures are kept in this window.", error: true);
                }
            }
            if (!_closed)
            {
                IsEnabled = wasEnabled;
                UpdateControls();
                try
                {
                    if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                    Activate();
                    PromptBox.Focus();
                    PromptBox.CaretIndex = PromptBox.Text.Length;
                }
                catch (InvalidOperationException) { restoreFailed = true; }
                if (restoreFailed) ShowStatus("A Launcher window could not be restored. Your draft is kept; switch back to Start Agent to continue.", error: true);
            }
        }
    }

    private static int WindowOwnerDepth(Window window)
    {
        var depth = 0;
        for (var owner = window.Owner; owner is not null; owner = owner.Owner) depth++;
        return depth;
    }

    private Task StopContextGathererAsync()
    {
        _contextGathererLifetime?.Cancel();
        return Task.CompletedTask;
    }

    private void ForgetLastCaptureSource()
    {
        _lastCaptureSource?.Dispose();
        _lastCaptureSource = null;
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint window, nint after, int left, int top, int width, int height, uint flags);
}
