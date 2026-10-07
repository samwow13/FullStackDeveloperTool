namespace FullStackLauncher.AgentBridge;

internal sealed partial class AgentBridgeCoordination
{
    internal object BindReplyInbox(string projectId, string sessionToken, string chatId)
    {
        var session = RequireSession(projectId, sessionToken);
        if (!Guid.TryParseExact(chatId, "D", out var parsed))
            throw new ArgumentException("Supply your actual current CODEX_THREAD_ID as a UUID. Do not guess a chat ID.");
        chatId = parsed.ToString("D");
        if (session.ReplyChatId is not null && session.ReplyChatId != chatId)
            throw new InvalidOperationException("This project session is already bound to another chat. Register a new session for your current chat.");
        session.ReplyChatId = chatId;
        return new
        {
            instanceId = InstanceId,
            projectId,
            sessionId = session.Id,
            chatId,
            status = "bound_to_mcp_reply_inbox",
            bindingSource = "agent_supplied_current_chat_id",
            nativeTurnInput = false,
            idleChatWakeup = false
        };
    }

    internal string? ReplyInboxChatId(string projectId, string sessionToken, bool requireBound = true)
    {
        var chatId = RequireSession(projectId, sessionToken).ReplyChatId;
        if (requireBound && chatId is null)
            throw new InvalidOperationException("Bind this project session to your actual current CODEX_THREAD_ID before reading or acknowledging replies.");
        return chatId;
    }
}
