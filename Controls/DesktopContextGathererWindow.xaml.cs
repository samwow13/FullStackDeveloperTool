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

public partial class DesktopContextGathererWindow : Window, IDashboardReplacementWindow
{
    private readonly int _maximumCaptures;
    private readonly int _maximumBytes;
    private DesktopRegionCapture? _capture;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource<IReadOnlyList<CodexAgentChatImage>?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ObservableCollection<CaptureItem> _confirmed = [];
    private CancellationTokenSource? _selectionCancellation;
    private DesktopContextSelectionWindow? _selectionWindow;
    private BitmapSource? _pending;
    private bool _capturing;
    private bool _encoding;
    private bool _closeRequested;
    private bool _allowClose;
    private bool _discardConfirmed;
    private bool _controllerSuppressed;
    private nint _originalExtendedStyle;
    private int _nextCaption = 1;
    private string? _selectionFailure;

    private sealed record CaptureItem(CodexAgentChatImage Attachment, BitmapSource Preview, int Bytes)
    {
        public string Caption => Attachment.Caption;
    }

    bool IDashboardReplacementWindow.IsDashboardReplacementReady => !_encoding;
    bool IDashboardReplacementWindow.IsDashboardReplacementClosePending => _closeRequested && (_capturing || _encoding);

    private DesktopContextGathererWindow(int maximumCaptures, int maximumBytes)
    {
        FullStackLauncher.Services.WindowTheme.Attach(this);
        _maximumCaptures = Math.Clamp(maximumCaptures, 1, CodexAgentChatImage.MaximumCount);
        _maximumBytes = Math.Clamp(maximumBytes, 1, CodexAgentChatImage.MaximumTotalBytes);
        InitializeComponent();
        Gallery.ItemsSource = _confirmed;
        var area = SystemParameters.WorkArea;
        MaxWidth = area.Width;
        MaxHeight = area.Height;
        Width = Math.Min(Width, area.Width);
        Height = Math.Min(Height, area.Height);
        Left = Math.Max(area.Left, area.Right - Width - 18);
        Top = Math.Max(area.Top, area.Bottom - Height - 18);
    }

    internal static async Task<IReadOnlyList<CodexAgentChatImage>?> GatherAsync(
        Window owner, int maximumCaptures, int maximumBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        owner.Dispatcher.VerifyAccess();
        if (maximumCaptures <= 0 || maximumBytes <= 0 || cancellationToken.IsCancellationRequested) return null;
        var window = new DesktopContextGathererWindow(maximumCaptures, maximumBytes);
        void OwnerClosed(object? sender, EventArgs args) => window.RequestClose();
        owner.Closed += OwnerClosed;
        using var registration = cancellationToken.Register(() =>
        {
            if (!window.Dispatcher.HasShutdownStarted)
                _ = window.Dispatcher.BeginInvoke(new Action(window.RequestClose));
        });
        try
        {
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
            window._capture?.Dispose();
            window._selectionCancellation?.Dispose();
            window._shutdown.Dispose();
        }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await BeginCaptureAsync();

    private async Task BeginCaptureAsync()
    {
        if (_capturing || _encoding || _closeRequested || _confirmed.Count >= _maximumCaptures) return;
        _pending = null;
        _selectionFailure = null;
        _capturing = true;
        _selectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        var capture = new DesktopRegionCapture(Dispatcher);
        _capture = capture;
        capture.SelectionStarting += Capture_SelectionStarting;
        capture.SelectionStarted += Capture_SelectionStarted;
        capture.SelectionChanged += Capture_SelectionChanged;
        capture.SelectionCanceled += Capture_SelectionCanceled;
        SetStatus("Escape cancels only the current selection.");
        UpdateView();
        try
        {
            var image = await capture.CaptureAsync(_selectionCancellation.Token);
            if (!_closeRequested && image is not null)
            {
                if (!image.IsFrozen) { image = image.CloneCurrentValue(); image.Freeze(); }
                _pending = image;
                SetStatus($"{image.PixelWidth:N0} × {image.PixelHeight:N0} pixels · Confirm this screenshot or retake it.");
            }
            else if (!_closeRequested && _selectionFailure is { } failure) SetStatus(failure, true);
            else if (!_closeRequested) SetStatus(_confirmed.Count > 0
                ? "Selection canceled. Confirmed screenshots are kept. Add another or continue to your prompt."
                : "Selection canceled. Choose Capture area to try again.");
        }
        catch (OperationCanceledException)
        {
            if (!_closeRequested && _selectionFailure is { } failure) SetStatus(failure, true);
            else if (!_closeRequested) SetStatus(_confirmed.Count > 0
                ? "Selection canceled. Confirmed screenshots are kept."
                : "Selection canceled. Choose Capture area to try again.");
        }
        catch (InvalidOperationException error) when (error.Message.Length <= 400 &&
            new[] { "Drag an area", "Desktop", "Display", "Physical", "Screen" }.Any(prefix => error.Message.StartsWith(prefix, StringComparison.Ordinal)))
        {
            if (!_closeRequested) SetStatus(error.Message, true);
        }
        catch (Exception)
        {
            if (!_closeRequested) SetStatus("The desktop area could not be captured. Return to the app and try Capture area again. Confirmed screenshots are kept.", true);
        }
        finally
        {
            CloseSelectionWindow();
            RestoreController();
            capture.SelectionStarting -= Capture_SelectionStarting;
            capture.SelectionStarted -= Capture_SelectionStarted;
            capture.SelectionChanged -= Capture_SelectionChanged;
            capture.SelectionCanceled -= Capture_SelectionCanceled;
            capture.Dispose();
            if (ReferenceEquals(_capture, capture)) _capture = null;
            _capturing = false;
            _selectionCancellation.Dispose();
            _selectionCancellation = null;
            UpdateView();
            if (_closeRequested) CompleteClose();
            else if (_pending is not null) Activate();
        }
    }

    private void Capture_SelectionStarting()
    {
        if (_closeRequested) return;
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
        var compact = _capturing || !reviewing && !hasConfirmed;
        Heading.Text = _capturing ? "Choose an area" : reviewing ? "Review screenshot" : hasConfirmed ? "Screenshots ready" : "Context Gatherer";
        Instructions.Text = _capturing
            ? "In any app, hold Ctrl and left-drag from top-left to bottom-right. Release the mouse button to review."
            : reviewing ? "Confirm the area before adding it to your prompt."
            : hasConfirmed ? "Add more screenshots, or continue to write your prompt."
            : "Capture an area from any desktop app. Your prompt stays in the launcher.";
        PreviewPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CapturePreview.Source = _pending ?? _confirmed.LastOrDefault()?.Preview;
        GalleryPanel.Visibility = hasConfirmed && !_capturing ? Visibility.Visible : Visibility.Collapsed;
        var used = _confirmed.Sum(item => item.Bytes);
        GallerySummary.Text = $"{_confirmed.Count} of {_maximumCaptures} screenshots · {used / 1_000_000d:0.#} / {_maximumBytes / 1_000_000d:0.#} MB";
        PauseButton.Visibility = _capturing ? Visibility.Visible : Visibility.Collapsed;
        RetakeButton.Visibility = DiscardButton.Visibility = ConfirmButton.Visibility = reviewing ? Visibility.Visible : Visibility.Collapsed;
        CaptureButton.Visibility = !_capturing && !reviewing ? Visibility.Visible : Visibility.Collapsed;
        CaptureButton.Content = hasConfirmed ? "Add another" : "Capture area";
        CaptureButton.IsEnabled = !_encoding && !_closeRequested && _confirmed.Count < _maximumCaptures && used < _maximumBytes;
        ContinueButton.Visibility = hasConfirmed && !reviewing ? Visibility.Visible : Visibility.Collapsed;
        ContinueButton.IsEnabled = !_encoding && !_closeRequested;
        ConfirmButton.IsEnabled = RetakeButton.IsEnabled = DiscardButton.IsEnabled = !_encoding && !_closeRequested;
        Gallery.IsEnabled = !_encoding && !_closeRequested;
        DiscardBatchButton.Visibility = hasConfirmed && !_capturing ? Visibility.Visible : Visibility.Collapsed;
        DiscardBatchButton.IsEnabled = !_encoding && !_closeRequested;
        CloseButton.Content = hasConfirmed ? "Finish with confirmed" : "Cancel gather";
        CloseButton.Visibility = ContinueButton.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        CloseButton.IsEnabled = !_closeRequested;
        if (_encoding) SetStatus("Preparing this screenshot…");
        var area = SystemParameters.WorkArea;
        var newWidth = Math.Min(compact ? 440 : 780, area.Width);
        var newHeight = Math.Min(compact ? 300 : 690, area.Height);
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

    private async void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (_pending is not { } source || _encoding || _capturing || _closeRequested) return;
        _encoding = true;
        UpdateView();
        try
        {
            var caption = $"Screenshot {_nextCaption}";
            var item = await Task.Run(() =>
            {
                var attachment = CodexAgentImageStaging.EncodeBitmap(source, caption);
                var bytes = Convert.FromBase64String(attachment.DataBase64).Length;
                var preview = CodexAgentImageStaging.CreatePreview(attachment, 1440);
                return new CaptureItem(attachment, preview, bytes);
            });
            if (_confirmed.Count >= _maximumCaptures || (long)_confirmed.Sum(capture => capture.Bytes) + item.Bytes > _maximumBytes)
            {
                SetStatus("These screenshots exceed the remaining attachment limit. Remove a confirmed screenshot or retake a smaller area.", true);
                return;
            }
            _confirmed.Add(item);
            _nextCaption++;
            _pending = null;
            SetStatus(_confirmed.Count >= _maximumCaptures
                ? "Screenshot limit reached. Continue to your prompt, or remove a screenshot."
                : "Screenshot confirmed. Add another area or continue to your prompt.");
        }
        catch (Exception)
        {
            SetStatus("This screenshot could not be prepared within the 8 MB image limit. Retake a smaller area. Confirmed screenshots are kept.", true);
        }
        finally
        {
            _encoding = false;
            UpdateView();
            if (_closeRequested) CompleteClose();
        }
    }

    private async void Capture_Click(object sender, RoutedEventArgs e) => await BeginCaptureAsync();
    private async void Retake_Click(object sender, RoutedEventArgs e) => await BeginCaptureAsync();
    private void Pause_Click(object sender, RoutedEventArgs e) => _selectionCancellation?.Cancel();
    private void Continue_Click(object sender, RoutedEventArgs e) => RequestClose();
    private void Close_Click(object sender, RoutedEventArgs e) => RequestClose();

    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        if (_encoding || _capturing) return;
        _pending = null;
        SetStatus("Unconfirmed capture discarded. Confirmed screenshots are kept.");
        UpdateView();
    }

    private void Thumbnail_Click(object sender, RoutedEventArgs e)
    {
        if (_pending is null && sender is FrameworkElement { Tag: CaptureItem item }) CapturePreview.Source = item.Preview;
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_encoding || _capturing || sender is not FrameworkElement { Tag: CaptureItem item }) return;
        _confirmed.Remove(item);
        SetStatus("Screenshot removed from this gather.");
        UpdateView();
    }

    private void DiscardBatch_Click(object sender, RoutedEventArgs e)
    {
        DiscardConfirmation.Visibility = Visibility.Visible;
        Actions.IsEnabled = ClosingActions.IsEnabled = false;
    }

    private void KeepBatch_Click(object sender, RoutedEventArgs e)
    {
        DiscardConfirmation.Visibility = Visibility.Collapsed;
        Actions.IsEnabled = ClosingActions.IsEnabled = true;
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
        if (DiscardConfirmation.Visibility == Visibility.Visible) { KeepBatch_Click(sender, e); return; }
        if (_capturing) _selectionCancellation?.Cancel();
        else if (_pending is not null) Discard_Click(sender, e);
    }

    private void RequestClose()
    {
        if (_allowClose || _closeRequested) return;
        _closeRequested = true;
        _shutdown.Cancel();
        _selectionCancellation?.Cancel();
        if (!_capturing && !_encoding) _ = Dispatcher.BeginInvoke(new Action(CompleteClose));
    }

    private void CompleteClose()
    {
        if (_capturing || _encoding || _allowClose) return;
        _allowClose = true;
        CloseSelectionWindow();
        RestoreController();
        Close();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        RequestClose();
    }

    private void Window_Closed(object? sender, EventArgs e) => _completion.TrySetResult(
        !_discardConfirmed && _confirmed.Count > 0 ? _confirmed.Select(item => item.Attachment).ToArray() : null);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);
}
