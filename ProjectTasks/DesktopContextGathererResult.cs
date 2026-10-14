namespace FullStackLauncher.ProjectTasks;

internal sealed record DesktopContextGathererResult(IReadOnlyList<CodexAgentChatImage> Images,
    DesktopCaptureResumeTarget? ResumeTarget);
