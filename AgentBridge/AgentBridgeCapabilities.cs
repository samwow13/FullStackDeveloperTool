using System.Reflection;

namespace FullStackLauncher.AgentBridge;

/// <summary>Public, value-free description of the dashboard bridge contract.</summary>
internal static class AgentBridgeCapabilities
{
    // Increment when the meaning or shape of this capability response changes.
    internal const int SchemaVersion = 1;
    internal const int BridgeRevision = 1;

    // Keep this list in sync with the public actions handled by MainWindow.AgentBridge.
    // Internal dashboard handoff operations are deliberately excluded.
    internal static readonly string[] Actions =
    [
        "capabilities", "agent_setup", "projects", "services", "git_connections", "git_status",
        "record_git_changes", "project_notes", "save_follow_up_note", "bind_reply_inbox", "read_reply_inbox", "ack_reply_inbox",
        "service_status", "recent_activity", "service_console", "reservation_status",
        "register_project", "declare_service_use", "release_service_use", "heartbeat_project",
        "unregister_project", "project_events", "claim_project", "wait_project", "cancel_wait",
        "release_project", "service_action"
    ];

    internal static string ApplicationVersion =>
        typeof(AgentBridgeCapabilities).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";
}
