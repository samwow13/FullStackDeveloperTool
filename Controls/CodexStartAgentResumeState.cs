using FullStackLauncher.ProjectTasks;

namespace FullStackLauncher.Controls;

/// <summary>Editable composer state, separate from durable submission receipts.</summary>
internal sealed record CodexStartAgentResumeState
{
    public string Text { get; init; } = "";
    public IReadOnlyList<CodexAgentChatImage> Images { get; init; } = [];
    public bool KeepAwake { get; init; }
    public bool UltraFastEnabled { get; init; }
    public bool FastEnabled { get; init; }
    public string? PreviousAttemptId { get; init; }
    public bool InspectSubmission { get; init; }
    public string? ContextDraft { get; init; }
    public string? ContextBaseline { get; init; }
    public string? ContextRevision { get; init; }
    public string? ModelId { get; init; }
    public string? ReasoningEffort { get; init; }
    public CodexAgentAccessMode? AccessMode { get; init; }
    public bool ContextExpanded { get; init; }
    public int CaretIndex { get; init; }
    public double PromptScrollOffset { get; init; }
    public DesktopContextGathererResumeState? Gatherer { get; init; }
}
