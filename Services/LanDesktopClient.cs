using System.IO;
using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows.Media.Imaging;
using FullStackLauncher.CompanionProtocol;

namespace FullStackLauncher.Services;

public sealed partial class LanCompanionClient
{
    /// <summary>Opens a separate stream using the already approved identity. Desktop input is never retried.</summary>
    public async Task<LanDesktopSession> OpenDesktopAsync(LanCompanionSession session, RemoteDesktopOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        LanDesktopSession.ValidateOptions(options);
        session.ThrowIfDisposed(this);
        if (!session.SupportsDesktop)
            throw new InvalidOperationException("Use an updated companion tray app in the remote PC's signed-in session, then reconnect. Background service supports management only.");

        return await Task.Run(async () =>
        {
            using var opening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.LifetimeToken);
            opening.CancelAfter(TimeSpan.FromSeconds(12));
            await ValidateDesktopTrustAsync(session, opening.Token).ConfigureAwait(false);
            var certificate = _store.GetControllerCertificate();
            if (!RemoteProtocol.Fingerprint(certificate).Equals(session.ControllerCertificateFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                certificate.Dispose();
                throw new InvalidOperationException("Controller identity changed. Reconnect before opening the desktop.");
            }
            var connection = new LanCompanionSession(this, session.Address, session.Trust, certificate);
            try
            {
                var response = await ExecuteOnceAsync(connection, Request(connection, "desktopOpen") with { DesktopOptions = options },
                    TimeSpan.FromSeconds(10), opening.Token).ConfigureAwait(false);
                if (!response.Capabilities.Contains("desktop-v1", StringComparer.Ordinal))
                    throw new InvalidOperationException("Companion did not confirm desktop support. Update it before trying again.");
                var stream = await connection.EnsureConnectionAsync(opening.Token).ConfigureAwait(false);
                opening.Token.ThrowIfCancellationRequested();
                return new LanDesktopSession(connection, stream, token => ValidateDesktopTrustAsync(connection, token),
                    session.LifetimeToken, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or SocketException or AuthenticationException or JsonException)
            {
                connection.Dispose();
                throw new InvalidOperationException("Desktop connection failed. Check the companion's sharing status and paired identity, then reconnect explicitly.", exception);
            }
            catch { connection.Dispose(); throw; }
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task ValidateDesktopTrustAsync(LanCompanionSession session, CancellationToken token)
    {
        await Task.Run(() =>
        {
            session.ThrowIfDisposed(this);
            var trust = _store.Find(session.DeviceId);
            if (trust is null || !trust.CertificateFingerprint.Equals(session.Trust.CertificateFingerprint, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Saved companion pairing was removed or changed. Desktop closed; review pairing before reconnecting.");
            if (!LanDiscoveryService.IsAllowedLanAddress(session.Address) || LanDiscoveryService.IsLocalAddress(session.Address))
                throw new InvalidOperationException("Desktop is no longer on a directly connected private IPv4 LAN. Refresh discovery before reconnecting.");
        }, token).WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
    }
}

public sealed record LanDesktopFrame(BitmapSource Bitmap, RemoteDesktopFrame Metadata);

/// <summary>Bounded frame decoding and ordered input on one pinned TLS connection; events run off the UI thread.</summary>
public sealed class LanDesktopSession : IAsyncDisposable
{
    private const int MaximumJpegBytes = 8 * 1024 * 1024;
    private const int MaximumPendingCommands = 128;
    private readonly LanCompanionSession _connection;
    private readonly SslStream _stream;
    private readonly Func<CancellationToken, Task> _validateTrust;
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationToken _token;
    private readonly CancellationTokenRegistration _closeRegistration;
    private readonly object _outboundGate = new();
    private readonly Queue<PendingCommand> _commands = new();
    private readonly Channel<byte> _sendSignal = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Channel<EncodedFrame> _frames = Channel.CreateBounded<EncodedFrame>(new BoundedChannelOptions(1)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Task[] _workers;
    private RemoteDesktopCommand? _pendingMove;
    private bool _heartbeat;
    private Task? _disposeTask;
    private int _disposed;
    private int _failed;
    private string? _failureMessage;
    private long _receivedBytes;
    private long _receivedFrames;
    private long _lastReceivedSequence;
    private long _lastPresentedSequence;
    private long _lastPresentedTimestamp;
    private bool _staleInputReleased;

    internal LanDesktopSession(LanCompanionSession connection, SslStream stream, Func<CancellationToken, Task> validateTrust,
        CancellationToken parentLifetime, CancellationToken callerLifetime)
    {
        _connection = connection;
        _stream = stream;
        _validateTrust = validateTrust;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(parentLifetime, callerLifetime, connection.LifetimeToken);
        _token = _lifetime.Token;
        _closeRegistration = _token.Register(() => _ = Task.Run(connection.DropConnection));
        _workers = [Task.Run(ReadFramesAsync), Task.Run(DecodeFramesAsync), Task.Run(WriteCommandsAsync), Task.Run(HeartbeatAsync), Task.Run(CheckTrustAsync)];
    }

    public event EventHandler<LanDesktopFrame>? FrameReceived;
    public event Action<string>? Faulted;
    public bool IsClosed => Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _failed) != 0 || _token.IsCancellationRequested;
    public string? FailureMessage => Volatile.Read(ref _failureMessage);
    /// <summary>Received JPEG payload bytes, excluding protocol metadata and TLS overhead.</summary>
    public long ReceivedBytes => Interlocked.Read(ref _receivedBytes);
    public long ReceivedFrames => Interlocked.Read(ref _receivedFrames);

    /// <summary>Call only after the viewer presents this frame. A frozen UI cannot retain held remote input.</summary>
    public void NotifyFramePresented(long sequence)
    {
        lock (_outboundGate)
        {
            if (IsClosed || sequence <= _lastPresentedSequence || sequence > Interlocked.Read(ref _lastReceivedSequence)) return;
            _lastPresentedSequence = sequence;
            _lastPresentedTimestamp = Stopwatch.GetTimestamp();
            _staleInputReleased = false;
        }
    }

    public bool Send(RemoteDesktopCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Kind == "settings") return UpdateOptions(command.Options ?? throw new ArgumentException("Desktop settings are required."));
        if (command.Kind == "release") return ReleaseInput();
        ValidateCommand(command);
        var overflow = false;
        lock (_outboundGate)
        {
            if (IsClosed) return false;
            if (command.Kind != "heartbeat" && (command.Sequence != _lastPresentedSequence || !HasFreshPresentedFrame())) return false;
            if (command.Kind == "move") _pendingMove = command;
            else if (command.Kind == "heartbeat") _heartbeat = true;
            else if (_commands.Count + (_pendingMove is null ? 0 : 1) >= MaximumPendingCommands) overflow = true;
            else
            {
                // Preserve movement before a click/key transition while replacing unsent movement bursts.
                if (_pendingMove is not null) { _commands.Enqueue(new(_pendingMove)); _pendingMove = null; }
                _commands.Enqueue(new(command));
            }
        }
        if (overflow) { Fail("Desktop input could not keep up. Connection closed to release held keys and buttons; reconnect explicitly."); return false; }
        _sendSignal.Writer.TryWrite(0);
        return true;
    }

    public bool UpdateOptions(RemoteDesktopOptions options)
    {
        ValidateOptions(options);
        lock (_outboundGate)
        {
            if (IsClosed) return false;
            ClearPendingInput();
            _lastPresentedTimestamp = 0;
            _commands.Enqueue(new(new RemoteDesktopCommand { Kind = "release" }));
            _commands.Enqueue(new(new RemoteDesktopCommand { Kind = "settings", Options = options }));
        }
        _sendSignal.Writer.TryWrite(0);
        return true;
    }

    public bool ReleaseInput()
    {
        lock (_outboundGate)
        {
            if (IsClosed) return false;
            // Keep settings changes; a focus loss must not discard the requested monitor or quality.
            var settings = _commands.Where(item => item.Command.Kind == "settings").LastOrDefault();
            ClearPendingInput();
            _commands.Enqueue(new(new RemoteDesktopCommand { Kind = "release" }));
            if (settings is not null) _commands.Enqueue(settings);
        }
        _sendSignal.Writer.TryWrite(0);
        return true;
    }

    private async Task ReadFramesAsync()
    {
        long previousSequence = 0;
        try
        {
            while (!_token.IsCancellationRequested)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_token);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                var metadata = await RemoteProtocol.ReadAsync<RemoteDesktopFrame>(_stream, timeout.Token).ConfigureAwait(false);
                ValidateFrame(metadata, previousSequence);
                var jpeg = new byte[metadata.JpegLength];
                await _stream.ReadExactlyAsync(jpeg, timeout.Token).ConfigureAwait(false);
                previousSequence = metadata.Sequence;
                Interlocked.Add(ref _receivedBytes, jpeg.Length);
                Interlocked.Increment(ref _receivedFrames);
                Interlocked.Exchange(ref _lastReceivedSequence, metadata.Sequence);
                _frames.Writer.TryWrite(new(metadata, jpeg));
            }
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception exception) { Fail(DescribeFailure(exception)); }
        finally { _frames.Writer.TryComplete(); }
    }

    private async Task DecodeFramesAsync()
    {
        try
        {
            await foreach (var frame in _frames.Reader.ReadAllAsync(_token).ConfigureAwait(false))
            {
                ValidateJpegDimensions(frame.Bytes, frame.Metadata.Width, frame.Metadata.Height);
                using var memory = new MemoryStream(frame.Bytes, writable: false);
                var decoder = new JpegBitmapDecoder(memory, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count != 1) throw new InvalidDataException("Desktop image contains unexpected frames.");
                var bitmap = decoder.Frames[0];
                if (bitmap.PixelWidth != frame.Metadata.Width || bitmap.PixelHeight != frame.Metadata.Height)
                    throw new InvalidDataException("Desktop image dimensions differ from its metadata.");
                bitmap.Freeze();
                if (!IsClosed) FrameReceived?.Invoke(this, new(bitmap, frame.Metadata));
            }
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception exception) { Fail(DescribeFailure(exception)); }
    }

    private async Task WriteCommandsAsync()
    {
        try
        {
            await foreach (var _ in _sendSignal.Reader.ReadAllAsync(_token).ConfigureAwait(false))
            {
                while (TakeCommand() is { } pending)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(2));
                    await RemoteProtocol.WriteAsync(_stream, pending.Command, timeout.Token).ConfigureAwait(false);
                    pending.Completion?.TrySetResult();
                    // A fast LAN must still coalesce high-polling mice instead of flooding the host.
                    // Transitions retain their order and wait for at most this movement interval.
                    if (pending.Command.Kind == "move") await Task.Delay(8, _token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception exception) { Fail(DescribeFailure(exception)); }
    }

    private PendingCommand? TakeCommand()
    {
        lock (_outboundGate)
        {
            if (_commands.TryDequeue(out var queued)) return queued;
            if (_heartbeat) { _heartbeat = false; return new(new RemoteDesktopCommand { Kind = "heartbeat", Sequence = HasFreshPresentedFrame() ? _lastPresentedSequence : 0 }); }
            if (_pendingMove is { } move) { _pendingMove = null; return new(move); }
            return null;
        }
    }

    private async Task HeartbeatAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            while (await timer.WaitForNextTickAsync(_token).ConfigureAwait(false))
            {
                lock (_outboundGate)
                {
                    if (!HasFreshPresentedFrame() && !_staleInputReleased)
                    {
                        ReleaseInput();
                        _staleInputReleased = true;
                    }
                }
                if (!Send(new RemoteDesktopCommand { Kind = "heartbeat" })) break;
            }
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception exception) { Fail(DescribeFailure(exception)); }
    }

    private async Task CheckTrustAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (await timer.WaitForNextTickAsync(_token).ConfigureAwait(false))
                await _validateTrust(_token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Fail(exception is InvalidOperationException ? exception.Message :
                exception is TimeoutException ? "Saved pairing could not be revalidated. Desktop closed; check the local pairing store before reconnecting." : DescribeFailure(exception));
        }
    }

    private void Fail(string message)
    {
        if (Interlocked.Exchange(ref _failed, 1) != 0 || Volatile.Read(ref _disposed) != 0) return;
        Volatile.Write(ref _failureMessage, message);
        // Send can run on the dispatcher. Close transport and notify listeners away from that thread.
        _ = Task.Run(() =>
        {
            try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
            _connection.DropConnection();
            try { Faulted?.Invoke(message); } catch { /* Consumer failure must not retain remote input. */ }
        });
    }

    public ValueTask DisposeAsync()
    {
        lock (_outboundGate)
        {
            Interlocked.Exchange(ref _disposed, 1);
            return new(_disposeTask ??= Task.Run(DisposeCoreAsync));
        }
    }

    private async Task DisposeCoreAsync()
    {
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_outboundGate)
        {
            ClearPendingInput();
            _commands.Enqueue(new(new RemoteDesktopCommand { Kind = "release" }, released));
        }
        _sendSignal.Writer.TryWrite(0);
        if (!_token.IsCancellationRequested)
        {
            try { await released.Task.WaitAsync(TimeSpan.FromMilliseconds(750)).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
        _lifetime.Cancel();
        _connection.DropConnection();
        try { await Task.WhenAll(_workers).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException) { }
        _closeRegistration.Dispose();
        _connection.Dispose();
        _lifetime.Dispose();
    }

    private void ClearPendingInput()
    {
        foreach (var pending in _commands) pending.Completion?.TrySetCanceled();
        _commands.Clear();
        _pendingMove = null;
        _heartbeat = false;
    }

    private bool HasFreshPresentedFrame() => _lastPresentedTimestamp != 0 &&
        Stopwatch.GetElapsedTime(_lastPresentedTimestamp) < TimeSpan.FromMilliseconds(1500);

    internal static void ValidateOptions(RemoteDesktopOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MonitorId is null || options.MonitorId.Length > 128 || options.MonitorId.Any(char.IsControl) || options.Revision < 0 ||
            options.MaxFramesPerSecond is < 1 or > 60 || options.MaxWidth is < 320 or > 3840 || options.JpegQuality is < 50 or > 95)
            throw new ArgumentException("Desktop settings must use 1-60 FPS, 320-3840 pixels, and JPEG quality 50-95.", nameof(options));
    }

    private static void ValidateCommand(RemoteDesktopCommand command)
    {
        if (command.Kind is not ("heartbeat" or "move" or "button" or "key" or "wheel") ||
            command.Kind is "move" or "button" or "wheel" && (!double.IsFinite(command.X) || !double.IsFinite(command.Y) || command.X is < 0 or > 1 || command.Y is < 0 or > 1) ||
            command.Kind is "move" or "button" or "key" or "wheel" && command.Sequence <= 0 ||
            command.Kind == "button" && command.Button is not ("left" or "right" or "middle" or "x1" or "x2") ||
            command.Kind == "key" && command.VirtualKey is < 8 or > 254 ||
            command.Kind == "wheel" && command.WheelDelta is < -1200 or > 1200)
            throw new ArgumentException("Desktop input command is invalid.", nameof(command));
    }

    private static void ValidateFrame(RemoteDesktopFrame frame, long previousSequence)
    {
        if (frame.Sequence <= previousSequence || frame.OptionsRevision < 0 || frame.MonitorId is null || frame.MonitorId.Length is < 1 or > 128 ||
            frame.Width is < 1 or > 7680 || frame.Height is < 1 or > 7680 || (long)frame.Width * frame.Height > 16_777_216 ||
            frame.SourceWidth is < 1 or > 16384 || frame.SourceHeight is < 1 or > 16384 || (long)frame.SourceWidth * frame.SourceHeight > 40_000_000 ||
            frame.Width > frame.SourceWidth || frame.Height > frame.SourceHeight || frame.JpegLength is < 4 or > MaximumJpegBytes ||
            !double.IsFinite(frame.CaptureMilliseconds) || frame.CaptureMilliseconds is < 0 or > 60000 ||
            frame.Monitors is null || frame.Monitors.Length is < 1 or > 32 ||
            frame.Monitors.Any(monitor => monitor is null || monitor.Id is null || monitor.Id.Length is < 1 or > 128 || monitor.Id.Any(char.IsControl) ||
                monitor.Name is null || monitor.Name.Length > 240 || monitor.Name.Any(char.IsControl) || monitor.Width is < 1 or > 16384 || monitor.Height is < 1 or > 16384 ||
                (long)monitor.Width * monitor.Height > 40_000_000 || monitor.Left is < -131072 or > 131072 || monitor.Top is < -131072 or > 131072) ||
            frame.Monitors.Select(monitor => monitor.Id).Distinct(StringComparer.Ordinal).Count() != frame.Monitors.Length ||
            !frame.Monitors.Any(monitor => monitor.Id == frame.MonitorId && monitor.Width == frame.SourceWidth && monitor.Height == frame.SourceHeight))
            throw new InvalidDataException("Companion returned invalid or oversized desktop metadata.");
    }

    private static void ValidateJpegDimensions(byte[] bytes, int width, int height)
    {
        if (bytes[0] != 0xff || bytes[1] != 0xd8) throw new InvalidDataException("Desktop frame is not a JPEG image.");
        var offset = 2;
        while (offset + 3 < bytes.Length)
        {
            if (bytes[offset++] != 0xff) break;
            while (offset < bytes.Length && bytes[offset] == 0xff) offset++;
            if (offset >= bytes.Length) break;
            var marker = bytes[offset++];
            if (marker is 0xd9 or 0xda) break;
            if (marker is 0xd8 or 0x01 or >= 0xd0 and <= 0xd7) continue;
            if (offset + 1 >= bytes.Length) break;
            var length = (bytes[offset] << 8) | bytes[offset + 1];
            if (length < 2 || offset + length > bytes.Length) break;
            if (marker is 0xc0 or 0xc1 or 0xc2)
            {
                if (length < 8 || bytes[offset + 2] != 8 || ((bytes[offset + 3] << 8) | bytes[offset + 4]) != height ||
                    ((bytes[offset + 5] << 8) | bytes[offset + 6]) != width)
                    throw new InvalidDataException("JPEG dimensions differ from bounded desktop metadata.");
                return;
            }
            offset += length;
        }
        throw new InvalidDataException("Desktop JPEG dimensions could not be verified before decoding.");
    }

    private static string DescribeFailure(Exception exception) => exception switch
    {
        InvalidDataException or JsonException or NotSupportedException => "Companion sent an invalid desktop frame. Connection closed; update both apps before reconnecting.",
        OperationCanceledException or TimeoutException => "Desktop connection stopped responding. Input stopped; companion releases held input on disconnect or timeout. Reconnect explicitly.",
        AuthenticationException => "Desktop identity verification failed. Review the paired companion before reconnecting.",
        _ => "Desktop connection ended. Check the remote PC and companion, then reconnect explicitly."
    };

    private sealed record EncodedFrame(RemoteDesktopFrame Metadata, byte[] Bytes);
    private sealed record PendingCommand(RemoteDesktopCommand Command, TaskCompletionSource? Completion = null);
}
