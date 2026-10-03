using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace FullStackLauncher.AgentBridge;

internal sealed class AgentBridgeHost : IDisposable
{
    private readonly string _pipeName;
    private readonly Func<AgentBridgeRequest, CancellationToken, Task<AgentBridgeResponse>> _handler;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Mutex _owner;
    private readonly bool _ownsMutex;

    internal AgentBridgeHost(string settingsPath,
        Func<AgentBridgeRequest, CancellationToken, Task<AgentBridgeResponse>> handler)
    {
        _pipeName = AgentBridgeProtocol.PipeName(settingsPath);
        _handler = handler;
        _owner = new Mutex(true, @"Local\" + _pipeName, out _ownsMutex);
        if (_ownsMutex) _ = Task.Run(ListenAsync);
    }

    internal bool IsActive => _ownsMutex;

    private async Task ListenAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_lifetime.Token);
                var connected = pipe;
                pipe = null;
                _ = ServeAsync(connected);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { break; }
            catch (IOException) when (_lifetime.IsCancellationRequested) { break; }
            catch (IOException)
            {
                try { await Task.Delay(500, _lifetime.Token); }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { break; }
            }
            finally { pipe?.Dispose(); }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        using (pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(33));
                var line = await reader.ReadLineAsync(timeout.Token);
                AgentBridgeResponse response;
                if (line is null || line.Length > 65_536)
                    response = AgentBridgeResponse.Failure("Bridge request is too large or incomplete.");
                else
                {
                    var request = JsonSerializer.Deserialize<AgentBridgeRequest>(line, AgentBridgeProtocol.Json);
                    response = request is null
                        ? AgentBridgeResponse.Failure("Bridge request is invalid.")
                        : await _handler(request, timeout.Token);
                }
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, AgentBridgeProtocol.Json));
            }
            catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException) { }
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        if (_ownsMutex) _owner.ReleaseMutex();
        _owner.Dispose();
        _lifetime.Dispose();
    }
}
