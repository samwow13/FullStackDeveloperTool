using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Threading;
using FullStackLauncher.Services;

namespace FullStackLauncher.Controls;

public partial class CodexStartAgentWindow
{
    private PromptDictationSession? _dictationSession;
    private DispatcherTimer? _microphoneMeterTimer;
    private bool _changingDictation;
    private bool _startingDictation;
    private bool _closingDictation;
    private Exception? _dictationError;
    private int _microphoneLevel;
    private long _microphoneLevelAt;
    private bool IsDictating => _dictationSession is not null || _changingDictation || _closingDictation;

    private void UpdateDictationControls()
    {
        var recording = _dictationSession is not null;
        RecordButton.Content = _changingDictation ? (_startingDictation ? "Starting…" : "Stopping…")
            : recording ? "Stop _recording" : "_Record";
        RecordButton.IsEnabled = !_closed && !_changingDictation && !_closingDictation && !_closingContext &&
            (recording || (!PromptBox.IsReadOnly && !_loadingAccess && !_loadingModels && !_dashboardResumeClose));
        RecordButton.Visibility = PendingAttemptId is null && _composerDraft is null ? Visibility.Visible : Visibility.Collapsed;
        RecordButton.Foreground = (Brush)FindResource(recording ? "AccentBrush" : "TextBrush");
        AutomationProperties.SetName(RecordButton, recording ? "Stop recording dictation" : "Record dictation");
        MicrophoneLevelPanel.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        if (!recording) SetMicrophoneLevel(0);
    }

    private async void Record_Click(object sender, RoutedEventArgs e)
    {
        if (!RecordButton.IsEnabled) return;
        if (_dictationSession is not null)
        {
            await StopDictationAsync();
            if (!_closed) PromptBox.Focus();
            return;
        }

        var session = new PromptDictationSession();
        _dictationSession = session;
        _dictationError = null;
        _changingDictation = true;
        _startingDictation = true;
        Interlocked.Exchange(ref _microphoneLevel, 0);
        Interlocked.Exchange(ref _microphoneLevelAt, 0);
        session.Hypothesized += text => PostDictation(session, () =>
            ShowDictationStatus(string.IsNullOrWhiteSpace(text) ? "Listening…" : text));
        session.Recognized += text => PostDictation(session, () => InsertDictatedText(text));
        session.AudioLevelUpdated += level =>
        {
            // Audio callbacks never enqueue dispatcher work. One timer samples the latest level.
            Interlocked.Exchange(ref _microphoneLevel, level);
            Interlocked.Exchange(ref _microphoneLevelAt, Environment.TickCount64);
        };
        session.Completed += error => PostDictation(session, () => FinishDictation(session, error));
        ShowDictationStatus("Opening microphone…");
        UpdateControls();
        try
        {
            await session.StartAsync();
            if (_closed || !ReferenceEquals(session, _dictationSession)) return;
            RecordButton.ToolTip = $"Stop dictation ({session.LanguageName}). Typing stays available.";
            ShowDictationStatus("Listening…");
            _microphoneMeterTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(80), DispatcherPriority.Background,
                (_, _) => RefreshMicrophoneLevel(), Dispatcher);
            _microphoneMeterTimer.Start();
            PromptBox.Focus();
        }
        catch (Exception ex)
        {
            var error = await ReleaseFailedDictationAsync(session, ex);
            if (!_closed)
            {
                FinishDictation(session, error);
                // A successful cleanup callback may already have cleared the session.
                _dictationError = error;
                ShowDictationStatus(SensitiveDataProtection.Redact(error.Message) + " Your message is kept.", error: true);
            }
        }
        finally
        {
            _startingDictation = false;
            _changingDictation = false;
            if (!_closed) UpdateControls();
        }
    }

    private void PostDictation(PromptDictationSession session, Action update)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            if (!_closed && ReferenceEquals(session, _dictationSession) &&
                PendingAttemptId is null && _composerDraft is null) update();
        }));
    }

    private void InsertDictatedText(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        // Insert after any selection instead of replacing typed text. Each phrase is undoable.
        var index = PromptBox.SelectionStart + PromptBox.SelectionLength;
        var existing = PromptBox.Text;
        var prefix = index > 0 && !char.IsWhiteSpace(existing[index - 1]) &&
            existing[index - 1] is not '(' and not '[' and not '{' ? " " : "";
        var suffix = index < existing.Length && !char.IsWhiteSpace(existing[index]) &&
            !char.IsPunctuation(existing[index]) ? " " : "";
        var insertion = prefix + text + suffix;
        PromptBox.BeginChange();
        try
        {
            PromptBox.Select(index, 0);
            PromptBox.SelectedText = insertion;
            PromptBox.Select(index + insertion.Length, 0);
        }
        finally { PromptBox.EndChange(); }
        var line = PromptBox.GetLineIndexFromCharacterIndex(PromptBox.CaretIndex);
        if (line >= 0) PromptBox.ScrollToLine(line);
        ShowDictationStatus("Listening…");
    }

    private async Task<bool> StopDictationAsync()
    {
        var session = _dictationSession;
        if (session is null) return _dictationError is null;
        _changingDictation = true;
        ShowDictationStatus("Finishing dictation…");
        UpdateControls();
        try
        {
            await session.StopAsync();
            // Final text and completion were posted in order. Drain them before freezing any draft.
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (ReferenceEquals(session, _dictationSession)) FinishDictation(session, null);
        }
        catch (Exception ex)
        {
            var error = await ReleaseFailedDictationAsync(session, ex);
            if (!_closed)
            {
                FinishDictation(session, error);
                _dictationError = error;
                ShowDictationStatus(SensitiveDataProtection.Redact(error.Message) + " Your message is kept.", error: true);
            }
        }
        finally
        {
            _changingDictation = false;
            if (!_closed) UpdateControls();
        }
        return _dictationError is null;
    }

    private void FinishDictation(PromptDictationSession session, Exception? error)
    {
        if (!ReferenceEquals(session, _dictationSession)) return;
        // A failed native release retains Stop and blocks submitting or replacing this draft.
        if (session.IsMicrophoneReleased) _dictationSession = null;
        _dictationError = error;
        if (_dictationSession is null)
        {
            _microphoneMeterTimer?.Stop();
            RecordButton.ToolTip = "Dictate with the default Windows microphone. Typing stays available.";
        }
        if (error is null) DictationPreview.Visibility = Visibility.Collapsed;
        else ShowDictationStatus(SensitiveDataProtection.Redact(error.Message) + " Your message is kept.", error: true);
        UpdateControls();
    }

    private void ShowDictationStatus(string text, bool error = false)
    {
        DictationPreview.Text = text;
        DictationPreview.Foreground = error ? new SolidColorBrush(Color.FromRgb(245, 161, 165))
            : (Brush)FindResource("MutedBrush");
        DictationPreview.MaxHeight = error ? double.PositiveInfinity : 64;
        DictationPreview.Visibility = Visibility.Visible;
        DictationPreview.ToolTip = text;
        AutomationProperties.SetLiveSetting(DictationPreview,
            error ? AutomationLiveSetting.Polite : AutomationLiveSetting.Off);
    }

    private void RefreshMicrophoneLevel()
    {
        var lastSample = Interlocked.Read(ref _microphoneLevelAt);
        var level = lastSample > 0 && Environment.TickCount64 - lastSample < 500
            ? Volatile.Read(ref _microphoneLevel) : 0;
        SetMicrophoneLevel(level);
    }

    private void SetMicrophoneLevel(int level)
    {
        MicrophoneLevel.Value = level;
        MicrophoneLevelText.Text = $"{level}%";
    }

    private void ReleaseDictationOnClose()
    {
        _microphoneMeterTimer?.Stop();
        var session = _dictationSession;
        _dictationSession = null;
        if (session is not null) _ = DisposeClosedDictationAsync(session);
    }

    private static async Task DisposeClosedDictationAsync(PromptDictationSession session)
    {
        try { await session.DisposeAsync(); }
        catch (Exception) { /* The window is gone; callbacks must not resurrect it. */ }
    }

    private static async Task<Exception> ReleaseFailedDictationAsync(PromptDictationSession session, Exception error)
    {
        try { await session.DisposeAsync(); }
        catch (Exception cleanupError)
        {
            return new InvalidOperationException(
                "Windows could not release the microphone. Try Stop recording again and check Windows microphone settings.",
                new AggregateException(error, cleanupError));
        }
        return error;
    }
}
