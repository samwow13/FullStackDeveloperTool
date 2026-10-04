using System.Diagnostics;
using System.IO;

namespace FullStackLauncher.Services;

internal sealed class ServiceOutputCapture : IDisposable
{
    private readonly StreamReader _output;
    private readonly StreamReader _error;
    private Process? _drain;

    internal ServiceOutputCapture(Process process, Action<string, bool> receive)
    {
        _output = process.StandardOutput;
        _error = process.StandardError;
        _ = ReadAsync(_output, false, receive);
        _ = ReadAsync(_error, true, receive);
    }

    private static async Task ReadAsync(StreamReader reader, bool error, Action<string, bool> receive)
    {
        try { while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line) receive(line, error); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    internal void PrepareKeepRunning()
    {
        if (_drain is not null && !_drain.HasExited) return;
        _drain?.Dispose();
        _drain = ServiceOutputDrain.Prepare(((FileStream)_output.BaseStream).SafeFileHandle,
            ((FileStream)_error.BaseStream).SafeFileHandle);
    }

    internal void CancelKeepRunningPreparation()
    {
        if (_drain is null) return;
        try { if (!_drain.HasExited) _drain.Kill(); }
        finally { _drain.Dispose(); _drain = null; }
    }

    internal void DisposeKeepingRunning()
    {
        _drain?.Dispose();
        _drain = null;
        _output.Dispose();
        _error.Dispose();
    }

    public void Dispose()
    {
        CancelKeepRunningPreparation();
        _output.Dispose();
        _error.Dispose();
    }
}
