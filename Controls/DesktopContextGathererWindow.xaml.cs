using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FullStackLauncher.ProjectTasks;
using FullStackLauncher.Services;

namespace FullStackLauncher.Controls;

internal enum DesktopCaptureView { OtherApps, Launcher, Prompt }

public partial class DesktopContextGathererWindow : Window, IDashboardReplacementWindow
{
    private readonly int _maximumCaptures;
    private readonly int _maximumBytes;
    private readonly Action<DesktopCaptureView> _changeCaptureView;
    private readonly Func<CancellationToken, Task<bool>>? _restoreSource;
    private bool _restoringSource;
    private DesktopCaptureResumeTarget? _transferredSource;
    private DesktopRegionCapture? _capture;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource<DesktopContextGathererResult?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ObservableCollection<CaptureItem> _confirmed = [];
    private CancellationTokenSource? _selectionCancellation;
    private DesktopContextSelectionWindow? _selectionWindow;
    private CaptureItem? _pending;
    private CaptureItem? _selectedCapture;
    private bool _capturing;
    private bool _captureEnabled = true;
    private bool _replacingPending;
    private bool _encoding;
    private bool _closeRequested;
    private bool _allowClose;
    private bool _discardConfirmed;
    private bool _controllerSuppressed;
    private nint _originalExtendedStyle;
    private int _nextCaption = 1;
    private string? _selectionFailure;
    private bool _changingCaptureView;
    private bool _syncingCaptureView = true;
    private DesktopCaptureView _captureView;
    private TaskCompletionSource? _captureStopped;

    private sealed record CaptureViewOption(DesktopCaptureView View, string Label);

    private sealed record CaptureItem(CodexAgentChatImage Attachment, BitmapSource Preview, int Bytes,
        DesktopCaptureResumeTarget? ResumeTarget)
    {
        public string Caption => Attachment.Caption;
    }

    bool IDashboardReplacementWindow.IsDashboardReplacementReady => !_encoding && !_changingCaptureView && !_restoringSource;
    bool IDashboardReplacementWindow.IsDashboardReplacementClosePending => _closeRequested && (_capturing || _encoding || _changingCaptureView || _restoringSource);

    private DesktopContextGathererWindow(int maximumCaptures, int maximumBytes, Action<DesktopCaptureView> changeCaptureView,
        DesktopCaptureView initialView, Func<CancellationToken, Task<bool>>? restoreSource,
        Action<DesktopContextGathererResumeState>? checkpointForReplacement)
    {
        FullStackLauncher.Services.WindowTheme.Attach(this);
        _maximumCaptures = Math.Clamp(maximumCaptures, 1, CodexAgentChatImage.MaximumCount);
        _maximumBytes = Math.Clamp(maximumBytes, 1, CodexAgentChatImage.MaximumTotalBytes);
        _changeCaptureView = changeCaptureView;
        _restoreSource = restoreSource;
        _checkpointForReplacement = checkpointForReplacement;
        _restoringSource = restoreSource is not null;
        _captureView = initialView;
        InitializeComponent();
        CaptureViewChoice.ItemsSource = new[]
        {
            new CaptureViewOption(DesktopCaptureView.OtherApps, "Other apps"),
            new CaptureViewOption(DesktopCaptureView.Launcher, "Launcher"),
            new CaptureViewOption(DesktopCaptureView.Prompt, "Prompt window")
        };
        CaptureViewChoice.SelectedItem = CaptureViewChoice.Items.OfType<CaptureViewOption>().First(item => item.View == initialView);
        _syncingCaptureView = false;
        var area = SystemParameters.WorkArea;
        MaxWidth = area.Width;
        MaxHeight = area.Height;
        Width = Math.Min(Width, area.Width);
        Height = Math.Min(Height, area.Height);
        Left = Math.Max(area.Left, area.Right - Width - 18);
        Top = Math.Max(area.Top, area.Bottom - Height - 18);
    }

    internal static async Task<DesktopContextGathererResult?> GatherAsync(
        Window owner, int maximumCaptures, int maximumBytes, Action<DesktopCaptureView> changeCaptureView,
        DesktopCaptureView initialView, Func<CancellationToken, Task<bool>>? restoreSource, CancellationToken cancellationToken,
        DesktopContextGathererResumeState? resumeState = null,
        Action<DesktopContextGathererResumeState>? checkpointForReplacement = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(changeCaptureView);
        owner.Dispatcher.VerifyAccess();
        if (maximumCaptures <= 0 || maximumBytes <= 0 || cancellationToken.IsCancellationRequested) return null;
        var window = new DesktopContextGathererWindow(maximumCaptures, maximumBytes, changeCaptureView, initialView,
            restoreSource, checkpointForReplacement);
        void OwnerClosed(object? sender, EventArgs args) => window.RequestClose();
        owner.Closed += OwnerClosed;
        using var registration = cancellationToken.Register(() =>
        {
            if (!window.Dispatcher.HasShutdownStarted)
                _ = window.Dispatcher.BeginInvoke(new Action(window.RequestClose));
        });
        try
        {
            if (resumeState is not null) window.RestoreDashboardResume(resumeState);
            // Deliberately unowned: minimizing the original modal composer must
            // not hide this controller or unwind the composer's ShowDialog.
            window.Show();
            return await window._completion.Task;
        }
        finally
        {
            owner.Closed -= OwnerClosed;
            window._shutdown.Cancel();
            window._selectionCancellation?.Cancel();
            window.CloseSelectionWindow();
            window.RestoreController();
            if (!window._completion.Task.IsCompleted)
            {
                // Restore validation can fail before Show. Release the unused WPF
                // window without scheduling close work against disposed lifetimes.
                window._allowClose = true;
                window.Close();
            }
            window._capture?.Dispose();
            window._selectionCancellation?.Dispose();
            window._shutdown.Dispose();
            window._pending?.ResumeTarget?.Dispose();
            foreach (var item in window._confirmed)
                if (!ReferenceEquals(item.ResumeTarget, window._transferredSource)) item.ResumeTarget?.Dispose();
        }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_restoredDashboardResume is { } restoredState)
        {
            _restoredDashboardResume = null;
            UpdateView();
            RestoreDashboardPlacement(restoredState);
            SetStatus(_captureEnabled ? "Screenshots restored. Hold Ctrl and drag to continue."
                : "Screenshots restored. Capture remains paused.");
        }
        if (_restoreSource is not null)
        {
            SetStatus("Returning to last capture…");
            UpdateView();
            try
            {
                var restored = await _restoreSource(_shutdown.Token);
                if (!_closeRequested) SetStatus(restored ? "Hold Ctrl and drag to capture."
                    : "The previous window or tab could not be fully restored. Choose where to capture next.");
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
            catch (Exception)
            {
                if (!_closeRequested) SetStatus("The previous window or tab could not be restored. Choose where to capture next.");
            }
            finally
            {
                _restoringSource = false;
                UpdateView();
            }
        }
        if (_closeRequested) CompleteClose();
        else await BeginCaptureAsync();
    }

    private async Task BeginCaptureAsync()
    {
        if (_capturing || _encoding || _changingCaptureView || _restoringSource || _closeRequested || _replacementPreparing ||
            _resumeCheckpointed || !_captureEnabled || !CanCaptureAnother ||
            DiscardConfirmation.Visibility == Visibility.Visible) return;
        _selectionFailure = null;
        _capturing = true;
        var captureStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _captureStopped = captureStopped;
        _selectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        var capture = new DesktopRegionCapture(Dispatcher, [new WindowInteropHelper(this).Handle]);
        _capture = capture;
        capture.SelectionStarting += Capture_SelectionStarting;
        capture.SelectionStarted += Capture_SelectionStarted;
        capture.SelectionChanged += Capture_SelectionChanged;
        capture.SelectionCanceled += Capture_SelectionCanceled;
        UpdateView();
        DesktopCaptureResumeTarget? unretainedSource = null;
        try
        {
            var result = await capture.CaptureAsync(_selectionCancellation.Token);
            unretainedSource = result?.ResumeTarget;
            if (!_closeRequested && result is not null)
            {
                var image = result.Image;
                if (!image.IsFrozen) { image = image.CloneCurrentValue(); image.Freeze(); }
                _encoding = true;
                UpdateView();
                var caption = _replacingPending && _pending is { } previous ? previous.Caption : $"Screenshot {_nextCaption}";
                // Prepare the final attachment before review so the displayed JSON,
                // including sent dimensions, is exactly what confirmation retains.
                var item = await Task.Run(() =>
                {
                    var attachment = CodexAgentImageStaging.EncodeBitmap(image, caption, result.Context);
                    var bytes = Convert.FromBase64String(attachment.DataBase64).Length;
                    var preview = CodexAgentImageStaging.CreatePreview(attachment, 1440);
                    return new CaptureItem(attachment, preview, bytes, result.ResumeTarget);
                });
                if (!_closeRequested)
                {
                    var retained = _replacingPending || _pending is null ? _confirmed.ToArray() : _confirmed.Append(_pending).ToArray();
                    if (ValidateCaptures(retained.Append(item).ToArray()))
                    {
                        if (!_replacingPending && _pending is { } accepted)
                        {
                            _confirmed.Add(accepted);
                        }
                        else if (_replacingPending) _pending?.ResumeTarget?.Dispose();
                        if (!_replacingPending || _pending is null) _nextCaption++;
                        _pending = item;
                        unretainedSource = null;
                        _selectedCapture = item;
                        _replacingPending = false;
                        SetStatus(CanCaptureAnother ? $"{image.PixelWidth:N0} × {image.PixelHeight:N0} pixels"
                            : "Screenshot limit reached. Continue, remove a screenshot, or retake.");
                    }
                    else _captureEnabled = false;
                }
            }
            else if (!_closeRequested && _selectionFailure is { } failure)
            {
                _captureEnabled = false;
                SetStatus(failure, true);
            }
            else if (!_closeRequested) SetStatus(_captureEnabled ? "Selection canceled. Screenshots kept." : "Capture paused. Screenshots kept.");
        }
        catch (OperationCanceledException)
        {
            if (!_closeRequested && _selectionFailure is { } failure)
            {
                _captureEnabled = false;
                SetStatus(failure, true);
            }
            else if (!_closeRequested) SetStatus("Selection canceled. Screenshots kept.");
        }
        catch (InvalidOperationException error) when (error.Message.Length <= 400 &&
            new[] { "Drag an area", "Desktop", "Display", "Physical", "Screen" }.Any(prefix => error.Message.StartsWith(prefix, StringComparison.Ordinal)))
        {
            if (!error.Message.StartsWith("Drag an area", StringComparison.Ordinal)) _captureEnabled = false;
            if (!_closeRequested) SetStatus(error.Message, true);
        }
        catch (Exception)
        {
            _captureEnabled = false;
            if (!_closeRequested) SetStatus(_encoding
                ? "This screenshot could not be prepared within the attachment limits. Retake a smaller area. Previous screenshots are kept."
                : "The desktop area could not be captured. Try Resume capture. Previous screenshots are kept.", true);
        }
        finally
        {
            unretainedSource?.Dispose();
            CloseSelectionWindow();
            RestoreController();
            capture.SelectionStarting -= Capture_SelectionStarting;
            capture.SelectionStarted -= Capture_SelectionStarted;
            capture.SelectionChanged -= Capture_SelectionChanged;
            capture.SelectionCanceled -= Capture_SelectionCanceled;
            capture.Dispose();
            if (ReferenceEquals(_capture, capture)) _capture = null;
            _capturing = false;
            _encoding = false;
            _selectionCancellation.Dispose();
            _selectionCancellation = null;
            if (ReferenceEquals(_captureStopped, captureStopped)) _captureStopped = null;
            captureStopped.TrySetResult();
            UpdateView();
            if (_closeRequested) CompleteClose();
            else QueueNextCapture();
        }
    }

    private bool CanCaptureAnother => _confirmed.Count + (!_replacingPending && _pending is not null ? 1 : 0) < _maximumCaptures &&
        (long)_confirmed.Sum(item => item.Bytes) + (!_replacingPending ? _pending?.Bytes ?? 0 : 0) < _maximumBytes;

    private bool CanEditCaptures => !_encoding && !_controllerSuppressed && !_changingCaptureView && !_restoringSource &&
        !_closeRequested && !_replacementPreparing && !_resumeCheckpointed;

    private void QueueNextCapture()
    {
        if (_captureEnabled && !_changingCaptureView && !_closeRequested && !_replacementPreparing && !_resumeCheckpointed &&
            !Dispatcher.HasShutdownStarted)
            _ = Dispatcher.BeginInvoke(new Action(async () => await BeginCaptureAsync()));
    }

    private async void CaptureView_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingCaptureView || CaptureViewChoice.SelectedItem is not CaptureViewOption choice) return;
        if (choice.View == _captureView) return;
        if (!CanEditCaptures || DiscardConfirmation.Visibility == Visibility.Visible)
        {
            RestoreCaptureViewChoice();
            return;
        }
        var resumeCapture = _captureEnabled;
        _changingCaptureView = true;
        _captureEnabled = false;
        SetStatus("Changing capture view…");
        UpdateView();
        try
        {
            // Drain native hooks before moving Launcher windows under the pointer.
            // Pending screenshots and the user's paused/running choice stay intact.
            var stopped = _captureStopped?.Task;
            _selectionCancellation?.Cancel();
            if (stopped is not null) await stopped;
            if (_closeRequested) return;
            _changeCaptureView(choice.View);
            _captureView = choice.View;
            _captureEnabled = resumeCapture;
            SetStatus(!CanCaptureAnother ? "Screenshot limit reached. Remove a screenshot or continue."
                : resumeCapture ? "Hold Ctrl and drag to capture." : "Capture paused. Screenshots kept.");
        }
        catch (Exception error)
        {
            // A visibility update can fail after restoring one of several windows.
            // Reapply the previous view while keeping capture paused.
            try { _changeCaptureView(_captureView); }
            catch (Exception) { }
            RestoreCaptureViewChoice();
            if (!_closeRequested)
                SetStatus("Capture view could not change. Capture is paused; screenshots are kept. " +
                    SensitiveDataProtection.Redact(error.Message), true);
        }
        finally
        {
            _changingCaptureView = false;
            UpdateView();
            if (_closeRequested) CompleteClose();
            else QueueNextCapture();
        }
    }

    private void RestoreCaptureViewChoice()
    {
        _syncingCaptureView = true;
        try
        {
            CaptureViewChoice.SelectedItem = CaptureViewChoice.Items.OfType<CaptureViewOption>()
                .First(choice => choice.View == _captureView);
        }
        finally { _syncingCaptureView = false; }
    }

    private bool ValidateCaptures(IReadOnlyList<CaptureItem> captures)
    {
        if (captures.Count > _maximumCaptures || captures.Sum(item => (long)item.Bytes) > _maximumBytes)
        {
            SetStatus("These screenshots exceed the remaining attachment limit. Remove a screenshot or retake a smaller area. Previous screenshots are kept.", true);
            return false;
        }
        try
        {
            _ = CodexAgentBrowserContext.BuildCaptureReference(captures.Select(item => item.Attachment).ToArray());
            return true;
        }
        catch (Exception)
        {
            SetStatus("This screenshot's context exceeds the attachment limits. Remove a screenshot or retake this area. Previous screenshots are kept.", true);
            return false;
        }
    }

    private void Capture_SelectionStarting()
    {
        if (_closeRequested) return;
        CaptureViewChoice.IsDropDownOpen = false;
        var handle = new WindowInteropHelper(this).Handle;
        _originalExtendedStyle = GetWindowLongPtr(handle, -20);
        SetWindowLongPtr(handle, -20, _originalExtendedStyle | 0x00000020 | 0x08000000);
        _controllerSuppressed = true;
        // Keep IsVisible true for guarded dashboard replacement enumeration.
        Opacity = 0;
        IsHitTestVisible = false;
    }

    private void Capture_SelectionStarted(DesktopCaptureFrame frame)
    {
        if (_closeRequested) return;
        CloseSelectionWindow();
        _selectionWindow = new DesktopContextSelectionWindow(frame);
        _selectionWindow.SelectionAbandoned += SelectionWindow_Abandoned;
        _selectionWindow.Show();
    }

    private void Capture_SelectionChanged(DesktopCaptureRectangle rectangle) => _selectionWindow?.UpdateSelection(rectangle);
    private void Capture_SelectionCanceled() => CloseSelectionWindow();
    private void SelectionWindow_Abandoned()
    {
        _selectionFailure = _selectionWindow?.FailureMessage;
        _selectionCancellation?.Cancel();
    }

    private void CloseSelectionWindow()
    {
        if (_selectionWindow is not { } selection) return;
        _selectionWindow = null;
        selection.SelectionAbandoned -= SelectionWindow_Abandoned;
        selection.CloseAfterCapture();
    }

    private void RestoreController()
    {
        if (!_controllerSuppressed) return;
        _controllerSuppressed = false;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != 0) SetWindowLongPtr(handle, -20, _originalExtendedStyle);
        Opacity = 1;
        IsHitTestVisible = true;
    }

    private void UpdateView()
    {
        var reviewing = _pending is not null;
        var hasConfirmed = _confirmed.Count > 0;
        var compact = !reviewing && !hasConfirmed;
        Heading.Text = _restoringSource ? "Returning to last capture" : _changingCaptureView ? "Changing capture view" : _encoding ? "Preparing screenshot" : _captureEnabled && CanCaptureAnother ? "Capture active"
            : reviewing || hasConfirmed ? "Screenshots ready" : "Capture paused";
        CaptureViewChoice.IsEnabled = CanEditCaptures && DiscardConfirmation.Visibility != Visibility.Visible;
        PreviewPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        if (_selectedCapture is null || !ReferenceEquals(_selectedCapture, _pending) && !_confirmed.Contains(_selectedCapture))
            _selectedCapture = _pending ?? _confirmed.LastOrDefault();
        CapturePreview.Source = _selectedCapture?.Preview;
        var context = _selectedCapture?.Attachment.CaptureContext;
        MetadataHeading.Text = $"{_selectedCapture?.Caption ?? "Captured context"} · JSON";
        ShowMetadataJson(context?.FormatForReview() ?? "{}");
        Gallery.ItemsSource = _pending is { } pending ? _confirmed.Append(pending).ToArray() : _confirmed.ToArray();
        GalleryPanel.Visibility = reviewing || hasConfirmed ? Visibility.Visible : Visibility.Collapsed;
        var used = _confirmed.Sum(item => item.Bytes) + (_pending?.Bytes ?? 0);
        GallerySummary.Text = $"{_confirmed.Count + (reviewing ? 1 : 0)} of {_maximumCaptures} screenshots · {used / 1_000_000d:0.#} / {_maximumBytes / 1_000_000d:0.#} MB";
        PauseButton.Visibility = _capturing ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.IsEnabled = CanEditCaptures;
        RetakeButton.Visibility = DiscardButton.Visibility = reviewing && ReferenceEquals(_selectedCapture, _pending)
            ? Visibility.Visible : Visibility.Collapsed;
        CaptureButton.Visibility = !_capturing ? Visibility.Visible : Visibility.Collapsed;
        CaptureButton.IsEnabled = CanEditCaptures && CanCaptureAnother;
        ContinueButton.Visibility = reviewing || hasConfirmed ? Visibility.Visible : Visibility.Collapsed;
        ContinueButton.IsEnabled = CanEditCaptures;
        RetakeButton.IsEnabled = DiscardButton.IsEnabled = CanEditCaptures;
        Gallery.IsEnabled = CanEditCaptures;
        DiscardBatchButton.Visibility = reviewing || hasConfirmed ? Visibility.Visible : Visibility.Collapsed;
        DiscardBatchButton.IsEnabled = CanEditCaptures;
        CloseButton.Content = hasConfirmed ? "Finish with confirmed" : "Cancel gather";
        CloseButton.Visibility = ContinueButton.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        CloseButton.IsEnabled = !_closeRequested && !_replacementPreparing;
        if (_encoding) SetStatus("Preparing this screenshot…");
        var area = SystemParameters.WorkArea;
        MinWidth = Math.Min(compact ? 340 : 640, area.Width);
        var newWidth = Math.Min(compact ? 440 : 780, area.Width);
        var newHeight = Math.Min(compact ? 300 : 790, area.Height);
        SizeToContent = compact ? SizeToContent.Height : SizeToContent.Manual;
        Width = newWidth;
        if (!compact) Height = newHeight;
        Left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - newWidth));
        Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - newHeight));
    }

    private void SetStatus(string text, bool error = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = error ? new SolidColorBrush(Color.FromRgb(255, 180, 180)) : (Brush)FindResource("MutedBrush");
    }

    private bool AcceptPendingCapture()
    {
        if (_pending is not { } item) return true;
        if (!ValidateCaptures(_confirmed.Append(item).ToArray())) return false;
        _confirmed.Add(item);
        _selectedCapture = item;
        _pending = null;
        return true;
    }

    private async void Capture_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditCaptures) return;
        _captureEnabled = true;
        SetStatus("");
        await BeginCaptureAsync();
    }

    private async void Retake_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditCaptures || _pending is null) return;
        _replacingPending = true;
        _captureEnabled = true;
        SetStatus("Retaking latest screenshot.");
        UpdateView();
        await BeginCaptureAsync();
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        _captureEnabled = false;
        _selectionCancellation?.Cancel();
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (CanEditCaptures && AcceptPendingCapture()) RequestClose();
    }
    private void Close_Click(object sender, RoutedEventArgs e) => RequestClose();

    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditCaptures) return;
        _pending?.ResumeTarget?.Dispose();
        _pending = null;
        _replacingPending = false;
        SetStatus("Unconfirmed capture discarded. Confirmed screenshots are kept.");
        UpdateView();
        QueueNextCapture();
    }

    private void Thumbnail_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditCaptures || sender is not FrameworkElement { Tag: CaptureItem item } ||
            !ReferenceEquals(item, _pending) && !_confirmed.Contains(item)) return;
        _selectedCapture = item;
        UpdateView();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditCaptures || sender is not FrameworkElement { Tag: CaptureItem item }) return;
        if (ReferenceEquals(item, _pending))
        {
            Discard_Click(sender, e);
            return;
        }
        if (_confirmed.Remove(item)) item.ResumeTarget?.Dispose();
        SetStatus("Screenshot removed from this gather.");
        UpdateView();
        QueueNextCapture();
    }

    private void DiscardBatch_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditCaptures) return;
        DiscardConfirmation.Visibility = Visibility.Visible;
        _selectionCancellation?.Cancel();
        Actions.IsEnabled = ClosingActions.IsEnabled = false;
        CaptureViewChoice.IsEnabled = false;
    }

    private void KeepBatch_Click(object sender, RoutedEventArgs e)
    {
        DiscardConfirmation.Visibility = Visibility.Collapsed;
        Actions.IsEnabled = ClosingActions.IsEnabled = true;
        CaptureViewChoice.IsEnabled = CanEditCaptures;
        QueueNextCapture();
    }

    private void ConfirmDiscardBatch_Click(object sender, RoutedEventArgs e)
    {
        _discardConfirmed = true;
        RequestClose();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _encoding) return;
        e.Handled = true;
        if (_restoringSource) { RequestClose(); return; }
        if (DiscardConfirmation.Visibility == Visibility.Visible) { KeepBatch_Click(sender, e); return; }
        if (_capturing) _selectionCancellation?.Cancel();
        else if (_pending is not null) Discard_Click(sender, e);
    }

    private void RequestClose()
    {
        if (_allowClose || _closeRequested) return;
        _closeRequested = true;
        _captureEnabled = false;
        _shutdown.Cancel();
        _selectionCancellation?.Cancel();
        if (_restoringSource) SetStatus("Canceling return to the previous capture…");
        if (!_capturing && !_encoding && !_changingCaptureView && !_restoringSource) _ = Dispatcher.BeginInvoke(new Action(CompleteClose));
    }

    private void CompleteClose()
    {
        if (_capturing || _encoding || _changingCaptureView || _restoringSource || _allowClose) return;
        _allowClose = true;
        CloseSelectionWindow();
        RestoreController();
        Close();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        if (_replacementPreparing) { e.Cancel = true; return; }
        e.Cancel = true;
        RequestClose();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        var result = !_resumeCheckpointed && !_discardConfirmed && _confirmed.Count > 0
            ? new DesktopContextGathererResult(_confirmed.Select(item => item.Attachment).ToArray(), _confirmed[^1].ResumeTarget)
            : null;
        if (_completion.TrySetResult(result)) _transferredSource = result?.ResumeTarget;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);
}
