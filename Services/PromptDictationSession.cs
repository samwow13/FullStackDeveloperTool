using System.Globalization;
using System.Runtime.InteropServices;
using System.Speech.Recognition;

namespace FullStackLauncher.Services;

/// <summary>
/// One explicit, local microphone dictation session. Event handlers run on worker threads
/// and must marshal UI updates without synchronously waiting for session operations.
/// </summary>
internal sealed class PromptDictationSession : IAsyncDisposable
{
    private static readonly TimeSpan FinalizationTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan CancellationTimeout = TimeSpan.FromSeconds(2);
    private readonly CultureInfo _preferredCulture;
    private readonly CultureInfo _fallbackCulture;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _stateLock = new();
    private readonly TaskCompletionSource<Exception?> _recognitionCompleted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SpeechRecognitionEngine? _engine;
    private bool _used;
    private bool _stopRequested;
    private bool _discardText;
    private bool _disposed;
    private bool _completionRaised;
    private bool _recognitionStarted;
    private bool _releaseAttempted;

    public PromptDictationSession(CultureInfo? preferredCulture = null)
    {
        _preferredCulture = preferredCulture ?? CultureInfo.CurrentUICulture;
        _fallbackCulture = CultureInfo.CurrentCulture;
    }

    public event Action<string>? Hypothesized;
    public event Action<string>? Recognized;
    public event Action<int>? AudioLevelUpdated;
    public event Action<Exception?>? Completed;

    public string? LanguageName { get; private set; }

    public bool IsMicrophoneReleased
    {
        get
        {
            lock (_stateLock)
                return _engine is null;
        }
    }

    public Task StartAsync() => Task.Run(async () =>
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_stateLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_used)
                    throw new InvalidOperationException("Create a new dictation session before recording again.");
                if (_stopRequested)
                    throw new OperationCanceledException("Recording was canceled before it started.");
                _used = true;
            }

            SpeechRecognitionEngine? engine = null;
            try
            {
                var recognizers = SpeechRecognitionEngine.InstalledRecognizers();
                var recognizer = recognizers.FirstOrDefault(item => item.Culture.Equals(_preferredCulture))
                    ?? recognizers.FirstOrDefault(item =>
                        item.Culture.TwoLetterISOLanguageName == _preferredCulture.TwoLetterISOLanguageName)
                    ?? recognizers.FirstOrDefault(item => item.Culture.Equals(_fallbackCulture))
                    ?? recognizers.FirstOrDefault(item => item.Culture.Name == "en-US")
                    ?? recognizers.FirstOrDefault();
                if (recognizer is null)
                    throw new InvalidOperationException(
                        "No Windows speech recognition engine is installed. Install speech recognition for your language in Windows Settings, then try Record again.");

                engine = new SpeechRecognitionEngine(recognizer);
                lock (_stateLock)
                    _engine = engine;
                engine.LoadGrammar(new DictationGrammar());
                engine.InitialSilenceTimeout = TimeSpan.Zero;
                engine.BabbleTimeout = TimeSpan.Zero;
                engine.EndSilenceTimeout = TimeSpan.FromMilliseconds(650);
                engine.EndSilenceTimeoutAmbiguous = TimeSpan.FromMilliseconds(900);
                engine.SpeechHypothesized += OnHypothesized;
                engine.SpeechRecognized += OnRecognized;
                engine.SpeechRecognitionRejected += OnRejected;
                engine.AudioLevelUpdated += OnAudioLevelUpdated;
                engine.RecognizeCompleted += OnRecognizeCompleted;
                lock (_stateLock)
                {
                    if (_disposed || _stopRequested)
                        throw new OperationCanceledException("Recording was canceled before it started.");
                    LanguageName = recognizer.Culture.DisplayName;
                }

                engine.SetInputToDefaultAudioDevice();
                lock (_stateLock)
                {
                    if (_disposed || _stopRequested)
                        throw new OperationCanceledException("Recording was canceled before it started.");
                }
                engine.RecognizeAsync(RecognizeMode.Multiple);
                _recognitionStarted = true;
            }
            catch (Exception ex) when (IsRecoverable(ex) || ex is OperationCanceledException)
            {
                var cleanupError = ReleaseEngine(engine);
                if (cleanupError is not null)
                    throw new InvalidOperationException(
                        IsMicrophoneReleased
                            ? "Recording could not start. The microphone was released; check Windows microphone and speech settings before recording again."
                            : "Recording could not start and Windows could not release the microphone. Click Stop recording to retry, and check Windows microphone settings.",
                        new AggregateException(ex, cleanupError));
                if (ex is OperationCanceledException)
                    throw;
                throw new InvalidOperationException(
                    ex is InvalidOperationException && engine is null
                        ? ex.Message
                        : "Recording could not start. Check the default microphone, desktop-app microphone permission, and installed speech recognition language in Windows Settings.",
                    ex);
            }
        }
        finally
        {
            _operationGate.Release();
        }
    });

    /// <summary>Stops recording after the current phrase is finalized, then releases the microphone.</summary>
    public Task StopAsync() => RequestStopAsync(cancel: false);

    /// <summary>Discards unfinished speech and releases the microphone.</summary>
    public Task CancelAsync() => RequestStopAsync(cancel: true);

    public ValueTask DisposeAsync()
    {
        lock (_stateLock)
        {
            _disposed = true;
            _discardText = true;
        }
        return new ValueTask(RequestStopAsync(cancel: true));
    }

    private Task RequestStopAsync(bool cancel)
    {
        lock (_stateLock)
        {
            _stopRequested = true;
            if (cancel)
                _discardText = true;
        }

        return Task.Run(async () =>
        {
            await _operationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                SpeechRecognitionEngine? engine;
                lock (_stateLock)
                    engine = _engine;
                if (engine is null)
                    return;

                Exception? error = null;
                try
                {
                    if (_recognitionStarted && !_releaseAttempted && !_recognitionCompleted.Task.IsCompleted)
                    {
                        if (cancel)
                            engine.RecognizeAsyncCancel();
                        else
                            engine.RecognizeAsyncStop();

                        if (!await WaitForCompletionAsync(cancel ? CancellationTimeout : FinalizationTimeout).ConfigureAwait(false))
                        {
                            lock (_stateLock)
                                _discardText = true;
                            error = new TimeoutException(
                                "Windows speech recognition took too long to stop. Recording was canceled; the last unfinished words may need to be recorded again.");
                            engine.RecognizeAsyncCancel();
                            await WaitForCompletionAsync(CancellationTimeout).ConfigureAwait(false);
                        }
                    }
                    if (_recognitionCompleted.Task.IsCompletedSuccessfully)
                        error ??= _recognitionCompleted.Task.Result;
                }
                catch (Exception ex) when (IsRecoverable(ex))
                {
                    error = RecognitionError(ex);
                }
                Finish(engine, error);
            }
            finally
            {
                _operationGate.Release();
            }
        });
    }

    private async Task<bool> WaitForCompletionAsync(TimeSpan timeout) =>
        await Task.WhenAny(_recognitionCompleted.Task, Task.Delay(timeout)).ConfigureAwait(false)
            == _recognitionCompleted.Task;

    private void OnHypothesized(object? sender, SpeechHypothesizedEventArgs args)
    {
        lock (_stateLock)
        {
            if (ReferenceEquals(sender, _engine) && !_discardText)
                Hypothesized?.Invoke(args.Result.Text);
        }
    }

    private void OnRecognized(object? sender, SpeechRecognizedEventArgs args)
    {
        lock (_stateLock)
        {
            if (ReferenceEquals(sender, _engine) && !_discardText)
                Recognized?.Invoke(args.Result.Text);
        }
    }

    private void OnRejected(object? sender, SpeechRecognitionRejectedEventArgs args)
    {
        lock (_stateLock)
        {
            if (ReferenceEquals(sender, _engine) && !_discardText)
                Hypothesized?.Invoke(string.Empty);
        }
    }

    private void OnAudioLevelUpdated(object? sender, AudioLevelUpdatedEventArgs args)
    {
        lock (_stateLock)
        {
            if (ReferenceEquals(sender, _engine) && !_discardText)
                AudioLevelUpdated?.Invoke(Math.Clamp(args.AudioLevel, 0, 100));
        }
    }

    private void OnRecognizeCompleted(object? sender, RecognizeCompletedEventArgs args)
    {
        SpeechRecognitionEngine engine;
        lock (_stateLock)
        {
            if (sender is not SpeechRecognitionEngine current || !ReferenceEquals(current, _engine))
                return;
            engine = current;
            _discardText = true;
            _recognitionCompleted.TrySetResult(args.Error is null ? null : RecognitionError(args.Error));
        }

        // Disposing inside a recognition callback can deadlock the native event worker.
        _ = Task.Run(async () =>
        {
            await _operationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                Finish(engine, _recognitionCompleted.Task.Result);
            }
            finally
            {
                _operationGate.Release();
            }
        });
    }

    private void Finish(SpeechRecognitionEngine engine, Exception? error)
    {
        lock (_stateLock)
        {
            if (!ReferenceEquals(engine, _engine))
                return;
        }
        var cleanupError = ReleaseEngine(engine);
        error = cleanupError ?? error;
        Action<Exception?>? completed;
        lock (_stateLock)
        {
            if (_completionRaised)
                return;
            // A failed native release retains this session for an explicit retry.
            // That retry must be able to report successful cleanup to the caller.
            _completionRaised = _engine is null;
            completed = Completed;
        }
        completed?.Invoke(error);
    }

    private Exception? ReleaseEngine(SpeechRecognitionEngine? engine)
    {
        lock (_stateLock)
        {
            _discardText = true;
        }
        if (engine is null)
            return null;

        _releaseAttempted = true;
        Exception? detachError = null;
        try
        {
            engine.SpeechHypothesized -= OnHypothesized;
            engine.SpeechRecognized -= OnRecognized;
            engine.SpeechRecognitionRejected -= OnRejected;
            engine.AudioLevelUpdated -= OnAudioLevelUpdated;
            engine.RecognizeCompleted -= OnRecognizeCompleted;
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            detachError = ex;
        }

        try
        {
            engine.Dispose();
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            return new InvalidOperationException(
                "Windows could not release the speech microphone. Click Stop recording to retry, and check Windows microphone settings.",
                detachError is null ? ex : new AggregateException(detachError, ex));
        }

        lock (_stateLock)
        {
            if (ReferenceEquals(engine, _engine))
                _engine = null;
        }
        return detachError is null ? null : new InvalidOperationException(
            "The microphone was released, but Windows could not detach a speech notification.", detachError);
    }

    private static Exception RecognitionError(Exception error) => new InvalidOperationException(
        "Recording stopped because Windows speech recognition failed. Check the microphone connection and Windows speech settings, then try Record again.", error);

    private static bool IsRecoverable(Exception error) => error is
        InvalidOperationException or ArgumentException or NotSupportedException or
        COMException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or
        System.IO.IOException or System.Security.SecurityException;
}
