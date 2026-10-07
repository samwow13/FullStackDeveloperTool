namespace FullStackLauncher.Models;

public enum CodexReplyInboxState { Pending, Read, Acknowledged }

/// <summary>A reply returned only to the agent session registered for its exact chat.</summary>
public sealed record CodexReplyInboxEntry(
    string ReplyId,
    string ChatId,
    string Text,
    DateTimeOffset CreatedAt,
    CodexReplyInboxState State,
    DateTimeOffset? ReadAt,
    DateTimeOffset? AcknowledgedAt);

/// <summary>Delivery metadata. Never includes reply text or its fingerprint.</summary>
public sealed record CodexReplyInboxReceipt(
    string ReplyId,
    string ChatId,
    DateTimeOffset CreatedAt,
    CodexReplyInboxState State,
    DateTimeOffset? ReadAt,
    DateTimeOffset? AcknowledgedAt);

/// <summary>Exact-chat counts and bounded metadata for outstanding and recently acknowledged replies.</summary>
public sealed record CodexReplyInboxStatus(
    string ChatId,
    int PendingCount,
    int ReadCount,
    int AcknowledgedCount,
    IReadOnlyList<CodexReplyInboxReceipt> Receipts,
    bool HasMoreAcknowledgedReceipts);
