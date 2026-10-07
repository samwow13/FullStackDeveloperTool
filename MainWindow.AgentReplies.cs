using System.IO;
using FullStackLauncher.AgentBridge;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class MainWindow
{
    private readonly CodexReplyInboxStore _agentReplyInbox = new(new SettingsStore().SettingsPath);
    private readonly CodexActivityProjectStore _agentChatProjects = new(new SettingsStore().SettingsPath);

    private async Task<object> RegisterAgentProjectAsync(AgentBridgeRequest request, CancellationToken token)
    {
        var project = RequireProject(request.ProjectId);
        if (request.ChatId is { } suppliedChatId)
        {
            var owner = (request.Owner ?? "").Trim();
            if (owner.Length > 80 || owner.Any(char.IsControl) || SensitiveDataProtection.ContainsLiteralCredential(owner))
                throw new ArgumentException("Owner or purpose must be short text without controls or credentials.");
            if (owner.Length == 0) throw new ArgumentException("Give this agent session a short owner label.");
            var chatId = CodexActivityProjectStore.CanonicalChatId(suppliedChatId);
            await Task.Run(() => _agentChatProjects.Save(chatId, project.Id, token), token);
            token.ThrowIfCancellationRequested();
            RequireProject(project.Id);
        }
        // Display ownership is durable; receiving replies still requires explicit inbox binding.
        return _agentCoordination.Register(project.Id, request.Owner ?? "");
    }

    private async Task<object> BindAgentReplyInboxAsync(AgentBridgeRequest request, CancellationToken token)
    {
        var project = RequireProject(request.ProjectId);
        var sessionToken = request.SessionToken ?? "";
        var previous = _agentCoordination.ReplyInboxChatId(project.Id, sessionToken, requireBound: false);
        var chatId = CodexActivityProjectStore.CanonicalChatId(request.ChatId ?? "");
        if (previous is not null && previous != chatId)
            throw new InvalidOperationException("This project session is already bound to another chat. Register a new session for your current chat.");
        await Task.Run(() => _agentChatProjects.Save(chatId, project.Id, token), token);
        token.ThrowIfCancellationRequested();
        RequireProject(project.Id);
        return _agentCoordination.BindReplyInbox(project.Id, sessionToken, chatId);
    }

    private async Task<object> ReadAgentReplyInboxAsync(AgentBridgeRequest request, CancellationToken token)
    {
        var project = RequireProject(request.ProjectId);
        var sessionToken = request.SessionToken ?? "";
        var chatId = _agentCoordination.ReplyInboxChatId(project.Id, sessionToken)!;
        token.ThrowIfCancellationRequested();
        var replies = await _agentReplyInbox.ReadAsync(chatId, token);
        token.ThrowIfCancellationRequested();
        RequireProject(project.Id);
        _agentCoordination.ReplyInboxChatId(project.Id, sessionToken);
        return BuildAgentReplyInboxEnvelope(project.Id, chatId, replies);
    }

    private async Task<object> AcknowledgeAgentReplyInboxAsync(AgentBridgeRequest request, CancellationToken token)
    {
        var project = RequireProject(request.ProjectId);
        var sessionToken = request.SessionToken ?? "";
        var chatId = _agentCoordination.ReplyInboxChatId(project.Id, sessionToken)!;
        var replyIds = request.ReplyIds ?? throw new ArgumentException("Supply the exact reply IDs you have read from this bound inbox.");
        token.ThrowIfCancellationRequested();
        var status = await _agentReplyInbox.AcknowledgeAsync(chatId, replyIds, token);
        token.ThrowIfCancellationRequested();
        RequireProject(project.Id);
        _agentCoordination.ReplyInboxChatId(project.Id, sessionToken);
        return new
        {
            instanceId = _agentCoordination.InstanceId,
            projectId = project.Id,
            chatId,
            status = "acknowledged_mcp_inbox_replies",
            status.PendingCount,
            status.ReadCount,
            status.AcknowledgedCount,
            status.HasMoreAcknowledgedReceipts,
            receipts = status.Receipts.Select(receipt => new
            {
                receipt.ReplyId,
                receipt.ChatId,
                receipt.CreatedAt,
                state = receipt.State.ToString(),
                receipt.ReadAt,
                receipt.AcknowledgedAt
            }).ToArray(),
            nativeTurnInput = false,
            idleChatWakeup = false
        };
    }

    private async Task<object> HeartbeatAgentProjectAsync(AgentBridgeRequest request, CancellationToken token)
    {
        var project = RequireProject(request.ProjectId);
        var sessionToken = request.SessionToken ?? "";
        var chatId = _agentCoordination.ReplyInboxChatId(project.Id, sessionToken, requireBound: false);
        object? inbox = null;
        if (chatId is not null)
        {
            try
            {
                var replies = await _agentReplyInbox.ReadAsync(chatId, token);
                inbox = BuildAgentReplyInboxEnvelope(project.Id, chatId, replies);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // Inbox failure must not consume a reply or suppress ordinary service restart warnings.
                inbox = new
                {
                    chatId,
                    status = "inbox_unavailable",
                    message = "The reply inbox could not be read. Pending replies are preserved; retry at the next safe boundary.",
                    nativeTurnInput = false,
                    idleChatWakeup = false
                };
            }
        }
        token.ThrowIfCancellationRequested();
        RequireProject(project.Id);
        return _agentCoordination.Heartbeat(project.Id, sessionToken, inbox);
    }

    private object BuildAgentReplyInboxEnvelope(string projectId, string chatId,
        IReadOnlyList<CodexReplyInboxEntry> entries) => new
    {
        instanceId = _agentCoordination.InstanceId,
        projectId,
        chatId,
        status = "read_from_mcp_reply_inbox",
        observedAt = DateTimeOffset.UtcNow,
        returnedCount = entries.Count,
        replies = entries.Select(reply => new
        {
            reply.ReplyId,
            reply.ChatId,
            reply.Text,
            reply.CreatedAt,
            state = reply.State.ToString(),
            reply.ReadAt,
            reply.AcknowledgedAt
        }).ToArray(),
        acknowledgeAfterReading = true,
        nativeTurnInput = false,
        idleChatWakeup = false
    };
}
