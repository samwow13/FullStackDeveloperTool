using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace FullStackLauncher.AgentBridge;

internal sealed class AgentBridgeRequest
{
    public string Action { get; set; } = "";
    public string? ProjectId { get; set; }
    public string? ServiceId { get; set; }
    public string? Operation { get; set; }
    public string? Owner { get; set; }
    public string? Purpose { get; set; }
    public string? LeaseToken { get; set; }
    public string? SessionToken { get; set; }
    public string? ChatId { get; set; }
    public string[]? ReplyIds { get; set; }
    public string? Ticket { get; set; }
    public string? RepositoryId { get; set; }
    public string? ConnectionId { get; set; }
    public string? Branch { get; set; }
    public string? UpdateId { get; set; }
    public string? Name { get; set; }
    public string? Prompt { get; set; }
    public string? Context { get; set; }
    public string? SourceTaskId { get; set; }
    public string? SourcePrompt { get; set; }
    public string? PageUrl { get; set; }
    public string? PageTitle { get; set; }
    public string[]? Bullets { get; set; }
    public int? Limit { get; set; }
    public int? Offset { get; set; }
    public int? WaitSeconds { get; set; }
    public long? SinceSequence { get; set; }
    public long? HandoffDeadlineUnixMilliseconds { get; set; }
}

internal sealed record AgentBridgeResponse(bool Ok, object? Data = null, string? Error = null)
{
    public static AgentBridgeResponse Success(object? data) => new(true, data);
    public static AgentBridgeResponse Failure(string error) => new(false, null, error);
}

internal static class AgentBridgeProtocol
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal const int MaximumRequestCharacters = 1_048_576;
    internal const int MaximumResponseCharacters = 262_144;

    internal static string PipeName(string settingsPath)
    {
        // Scope both ends to one Windows user, desktop session, and exact settings store.
        var user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var session = Process.GetCurrentProcess().SessionId;
        var identity = $"{user}|{session}|{Path.GetFullPath(settingsPath).ToUpperInvariant()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return "FullStackLauncher.AgentBridge." + Convert.ToHexString(hash[..16]);
    }

    internal static async Task<AgentBridgeResponse> SendAsync(string settingsPath, AgentBridgeRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = JsonSerializer.Serialize(request, Json);
            if (payload.Length > MaximumRequestCharacters)
                return AgentBridgeResponse.Failure("Launcher bridge request exceeds the supported size. Shorten the supplied prompt or context and retry.");
            using var pipe = new NamedPipeClientStream(".", PipeName(settingsPath), PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(35));
            await pipe.ConnectAsync(1500, timeout.Token);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(payload);
            var line = await reader.ReadLineAsync(timeout.Token);
            if (line is null || line.Length > MaximumResponseCharacters)
                return AgentBridgeResponse.Failure("Launcher bridge returned no valid response.");
            return JsonSerializer.Deserialize<AgentBridgeResponse>(line, Json)
                ?? AgentBridgeResponse.Failure("Launcher bridge returned no valid response.");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException
                                   or UnauthorizedAccessException or JsonException)
        {
            return AgentBridgeResponse.Failure("Running launcher dashboard unavailable for this Windows user and settings file. Open the dashboard and retry.");
        }
    }
}
