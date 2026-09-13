using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace FullStackLauncher.ProjectTasks;

/// <summary>An owned, hidden native App Server child; never a connection to the desktop daemon.</summary>
internal sealed class CodexAppServerConnection : IAsyncDisposable
{
    private const int MaximumLineLength = 1024 * 1024;
    private const int MaximumBufferedCharacters = 8 * 1024 * 1024;
    internal const string UnavailableMessage = "Codex connection is unavailable. Check installation, sign-in, model access, and configuration in Codex.";
    internal const string IncompatibleMessage = "Codex returned an unsupported response. Update Codex and try again.";
    private readonly Process _process;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _requests = new();
    private readonly Channel<(JsonElement Message, int Length)> _notifications = Channel.CreateBounded<(JsonElement, int)>(
        new BoundedChannelOptions(512) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task _reader;
    private readonly Task _stderrDrain;
    private Exception? _failure;
    private int _requestId;
    private int _bufferedCharacters;
    private int _disposed;

    private CodexAppServerConnection(Process process)
    {
        _process = process;
        _reader = ReadMessagesAsync();
        _stderrDrain = DrainErrorAsync();
    }

    public static async Task<CodexAppServerConnection> StartAsync(string folder, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var process = new Process();
        CodexAppServerConnection? connection = null;
        try
        {
            process.StartInfo = CreateStartInfo(FindExecutable(), folder);
            if (!process.Start())
                throw new InvalidOperationException(UnavailableMessage);
            connection = new CodexAppServerConnection(process);
            await connection.RequestAsync("initialize", new
            {
                clientInfo = new
                {
                    name = "full_stack_launcher",
                    title = "Full Stack Launcher",
                    version = typeof(CodexAppServerConnection).Assembly.GetName().Version?.ToString() ?? "1.0"
                }
            }, token).ConfigureAwait(false);
            await connection.SendAsync(new { method = "initialized", @params = new { } }, token).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            if (connection is not null)
                await connection.DisposeAsync().ConfigureAwait(false);
            else
                process.Dispose();
            throw;
        }
    }

    public async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken token, Action? beforeSend = null)
    {
        token.ThrowIfCancellationRequested();
        ThrowIfUnavailable();
        var id = Interlocked.Increment(ref _requestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_requests.TryAdd(id, completion))
            throw new InvalidOperationException(IncompatibleMessage);
        try
        {
            ThrowIfUnavailable();
            await SendAsync(new { id, method, @params = parameters }, token, beforeSend).ConfigureAwait(false);
            return await completion.Task.WaitAsync(token).ConfigureAwait(false);
        }
        finally
        {
            _requests.TryRemove(id, out _);
        }
    }

    public async Task<JsonElement> ReadNotificationAsync(CancellationToken token)
    {
        try
        {
            var entry = await _notifications.Reader.ReadAsync(token).ConfigureAwait(false);
            Interlocked.Add(ref _bufferedCharacters, -entry.Length);
            // A denied server request or broken transport always overrides buffered success events.
            ThrowIfUnavailable();
            return entry.Message;
        }
        catch (ChannelClosedException)
        {
            ThrowIfUnavailable();
            throw new IOException(UnavailableMessage);
        }
    }

    private async Task SendAsync(object message, CancellationToken token, Action? beforeSend = null)
    {
        var serialized = JsonSerializer.Serialize(message);
        if (serialized.Length > MaximumLineLength)
            throw new InvalidOperationException(IncompatibleMessage);
        await _writeLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            token.ThrowIfCancellationRequested();
            beforeSend?.Invoke();
            await _process.StandardInput.WriteLineAsync(serialized.AsMemory(), token).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(token).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    private async Task ReadMessagesAsync()
    {
        try
        {
            var reader = new JsonLineReader(_process.StandardOutput);
            while (!_lifetime.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(_lifetime.Token).ConfigureAwait(false);
                if (line is null)
                    throw new IOException(UnavailableMessage);
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException(IncompatibleMessage);
                if (root.TryGetProperty("method", out var method))
                {
                    if (method.ValueKind != JsonValueKind.String)
                        throw new InvalidOperationException(IncompatibleMessage);
                    if (root.TryGetProperty("id", out var serverId))
                    {
                        // Never approve an action, answer an elicitation, or run a dynamic tool.
                        await SendAsync(new
                        {
                            id = serverId.Clone(),
                            error = new { code = -32601, message = "This client does not support server requests." }
                        }, _lifetime.Token).ConfigureAwait(false);
                        throw new CodexInteractionRequiredException();
                    }
                    var buffered = Interlocked.Add(ref _bufferedCharacters, line.Length);
                    if (buffered > MaximumBufferedCharacters || !_notifications.Writer.TryWrite((root.Clone(), line.Length)))
                        throw new InvalidOperationException(IncompatibleMessage);
                    continue;
                }

                if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var responseId)
                    || responseId <= 0 || responseId > Volatile.Read(ref _requestId))
                    throw new InvalidOperationException(IncompatibleMessage);
                // A late response to a canceled request cannot acknowledge a subsequent request.
                if (!_requests.TryRemove(responseId, out var pending))
                    continue;
                if (root.TryGetProperty("error", out var error))
                {
                    var code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var codeValue)
                        && codeValue.ValueKind == JsonValueKind.Number && codeValue.TryGetInt32(out var parsed) ? parsed : 0;
                    pending.TrySetException(new CodexRequestException(code));
                }
                else if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
                    pending.TrySetResult(result.Clone());
                else
                    pending.TrySetException(new InvalidOperationException(IncompatibleMessage));
            }
        }
        catch (Exception ex)
        {
            var safe = ex is CodexInteractionRequiredException ? ex
                : ex is JsonException or InvalidOperationException ? new InvalidOperationException(IncompatibleMessage)
                : new IOException(UnavailableMessage);
            Interlocked.CompareExchange(ref _failure, safe, null);
            foreach (var request in _requests.Values)
                request.TrySetException(safe);
            _notifications.Writer.TryComplete(safe);
        }
    }

    private void ThrowIfUnavailable()
    {
        if (Volatile.Read(ref _failure) is { } failure)
            throw failure;
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private async Task DrainErrorAsync()
    {
        try
        {
            var buffer = new char[4096];
            while (await _process.StandardError.ReadAsync(buffer.AsMemory(), _lifetime.Token).ConfigureAwait(false) != 0) { }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            _process.StandardInput.Close();
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _process.WaitForExitAsync(shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or IOException)
        {
            KillOwnedProcess(_process);
        }
        await Task.WhenAll(_reader, _stderrDrain).ConfigureAwait(false);
        _process.Dispose();
        _lifetime.Dispose();
        // Concurrent canceled requests can still be leaving SendAsync. Do not dispose their semaphore.
    }

    private static ProcessStartInfo CreateStartInfo(string executable, string folder)
    {
        var info = new ProcessStartInfo
        {
            FileName = executable, WorkingDirectory = folder, UseShellExecute = false,
            CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        info.ArgumentList.Add("app-server");
        info.ArgumentList.Add("--listen");
        info.ArgumentList.Add("stdio://");
        return info;
    }

    private static string FindExecutable()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var candidate = ExecutableIn(directory.Trim().Trim('"'));
            if (candidate is not null) return candidate;
        }
        var bin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        var direct = ExecutableIn(bin);
        if (direct is not null) return direct;
        if (Directory.Exists(bin))
        {
            foreach (var directory in new DirectoryInfo(bin).EnumerateDirectories().OrderByDescending(item => item.LastWriteTimeUtc))
            {
                var candidate = ExecutableIn(directory.FullName);
                if (candidate is not null) return candidate;
            }
        }
        throw new InvalidOperationException("Codex was not found. Install the Windows Codex app or add a native codex.exe to PATH, then reopen the launcher.");
    }

    private static string? ExecutableIn(string directory)
    {
        try
        {
            if (!Path.IsPathFullyQualified(directory)) return null;
            var candidate = Path.GetFullPath(Path.Combine(directory, "codex.exe"));
            return File.Exists(candidate) ? candidate : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { return null; }
    }

    private static void KillOwnedProcess(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or AggregateException) { }
    }

    private sealed class JsonLineReader(StreamReader reader)
    {
        private readonly char[] _buffer = new char[4096];
        private int _position;
        private int _count;
        public async Task<string?> ReadLineAsync(CancellationToken token)
        {
            var line = new StringBuilder();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (_position == _count)
                {
                    _count = await reader.ReadAsync(_buffer.AsMemory(), token).ConfigureAwait(false);
                    _position = 0;
                    if (_count == 0) return line.Length == 0 ? null : line.ToString();
                }
                var character = _buffer[_position++];
                if (character == '\n') return line.ToString().TrimEnd('\r');
                if (line.Length >= MaximumLineLength) throw new InvalidOperationException(IncompatibleMessage);
                line.Append(character);
            }
        }
    }
}

internal sealed class CodexInteractionRequiredException() : InvalidOperationException(
    "Codex requested approval, input, or a tool that this connection check cannot handle. Open Codex to review it; the check did not approve the request.") { }

internal sealed class CodexRequestException(int code) : InvalidOperationException(code switch
{
    -32601 or -32602 => CodexAppServerConnection.IncompatibleMessage,
    -32001 => "Codex is busy. Wait briefly and explicitly try again.",
    _ => CodexAppServerConnection.UnavailableMessage
}) { }
