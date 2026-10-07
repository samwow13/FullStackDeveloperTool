using System.Text.Json;

namespace FullStackLauncher.ProjectTasks;

public enum CodexAgentAccessMode { Workspace, FullAccess }

/// <summary>Frozen access choices for an independent, noninteractive Launcher chat.</summary>
public static class CodexAgentAccessPolicy
{
    // This client cannot display approval requests. Never request an approval
    // channel that will fail as soon as a command needs additional permissions.
    internal const string ApprovalPolicy = "never";

    public static async Task<CodexAgentAccessMode> LoadConfiguredDefaultAsync(string folder,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await using var connection = await CodexAppServerConnection.StartAsync(folder, timeout.Token)
            .ConfigureAwait(false);
        // The composer presents an explicit access choice before submission.
        return await LoadConfiguredDefaultAsync(connection, folder, timeout.Token,
            requireSupportedMode: false).ConfigureAwait(false);
    }

    internal static async Task<CodexAgentAccessMode> LoadConfiguredDefaultAsync(
        CodexAppServerConnection connection, string folder, CancellationToken cancellationToken,
        bool requireSupportedMode = true)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var response = await connection.RequestAsync("config/read", new { includeLayers = false, cwd = folder },
            timeout.Token).ConfigureAwait(false);
        if (!response.TryGetProperty("config", out var config) || config.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException(CodexAppServerConnection.IncompatibleMessage);

        // Read only the two permission settings. Never inspect credentials,
        // persist the returned configuration, or change the user's Codex config.
        var sandbox = Text(config, "sandbox_mode");
        if (sandbox == "danger-full-access" && Text(config, "approval_policy") == "never")
            return CodexAgentAccessMode.FullAccess;
        if (!requireSupportedMode || sandbox is "workspace-write" or "danger-full-access")
            return CodexAgentAccessMode.Workspace;
        // Automatic dispatch has no human access-choice review. Never broaden
        // a read-only or unreported permission profile to workspace writes.
        throw new CodexCommandAccessException(sandbox == "read-only"
            ? "Codex is configured for read-only access. Queue dispatch requires an explicit workspace or full-access choice in Codex. No task or prompt was submitted."
            : "Codex did not report a supported configured access mode. Review its permission profile before starting the queue. No task or prompt was submitted.");
    }

    internal static string RequestedSandbox(CodexAgentAccessMode mode) => mode switch
    {
        CodexAgentAccessMode.Workspace => "workspace-write",
        CodexAgentAccessMode.FullAccess => "danger-full-access",
        _ => throw new ArgumentException("Choose a supported agent access mode.")
    };

    internal static string EffectiveSandbox(CodexAgentAccessMode mode) => mode switch
    {
        CodexAgentAccessMode.Workspace => "workspaceWrite",
        CodexAgentAccessMode.FullAccess => "dangerFullAccess",
        _ => throw new ArgumentException("Choose a supported agent access mode.")
    };

    private static string? Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
}
