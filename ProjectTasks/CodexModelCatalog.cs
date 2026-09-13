using System.ComponentModel;
using System.IO;
using System.Text.Json;

namespace FullStackLauncher.ProjectTasks;

public sealed record CodexReasoningEffortOption(string Id, string Description);

// Id is the wire model name, not model/list's separate catalog-entry identifier.
public sealed record CodexModelOption(
    string Id,
    string DisplayName,
    string DefaultReasoningEffort,
    IReadOnlyList<CodexReasoningEffortOption> SupportedReasoningEfforts,
    bool IsDefault = false);

/// <summary>Explicit model discovery through an owned App Server connection; never dispatches work.</summary>
public sealed class CodexModelCatalog
{
    private const int MaximumPages = 20;
    private const string IncompatibleMessage =
        "Codex returned an unsupported model catalog. Update Codex, then refresh models.";

    public async Task<IReadOnlyList<CodexModelOption>> LoadAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await using var connection = await CodexAppServerConnection.StartAsync(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), timeout.Token).ConfigureAwait(false);
            return await LoadAsync(connection, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception) when (timeout.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("Codex model discovery timed out. Check Codex sign-in and connectivity, then refresh models.");
        }
        catch (JsonException) { throw new InvalidOperationException(IncompatibleMessage); }
        catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new InvalidOperationException(CodexAppServerConnection.UnavailableMessage);
        }
    }

    internal static async Task<IReadOnlyList<CodexModelOption>> LoadAsync(
        CodexAppServerConnection connection, CancellationToken token)
    {
        var models = new List<CodexModelOption>();
        var modelIds = new HashSet<string>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        for (var page = 0; page < MaximumPages; page++)
        {
            var result = await connection.RequestAsync("model/list",
                new { limit = 100, includeHidden = false, cursor }, token).ConfigureAwait(false);
            if (!result.TryGetProperty("data", out var entries) || entries.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException(IncompatibleMessage);
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) throw new InvalidOperationException(IncompatibleMessage);
                if (entry.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True) continue;
                var model = ParseModel(entry);
                if (modelIds.Add(model.Id)) models.Add(model);
            }
            cursor = ReadOptionalString(result, "nextCursor");
            if (cursor is null)
            {
                if (models.Count == 0)
                    throw new InvalidOperationException("Codex returned no available models. Check sign-in and model access in Codex, then refresh models.");
                return models.AsReadOnly();
            }
            if (!cursors.Add(cursor)) throw new InvalidOperationException(IncompatibleMessage);
        }
        throw new InvalidOperationException("Codex returned too many model pages. Update Codex, then refresh models.");
    }
    private static CodexModelOption ParseModel(JsonElement entry)
    {
        var id = ReadRequiredString(entry, "model");
        var name = ReadRequiredString(entry, "displayName");
        var defaultEffort = ReadRequiredString(entry, "defaultReasoningEffort");
        if (!entry.TryGetProperty("supportedReasoningEfforts", out var options) || options.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException(IncompatibleMessage);

        var efforts = new List<CodexReasoningEffortOption>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var option in options.EnumerateArray())
        {
            var effort = ReadRequiredString(option, "reasoningEffort");
            if (ids.Add(effort))
                efforts.Add(new CodexReasoningEffortOption(effort, ReadOptionalString(option, "description") ?? effort));
        }
        if (!ids.Contains(defaultEffort))
            throw new InvalidOperationException(IncompatibleMessage);

        var isDefault = entry.TryGetProperty("isDefault", out var value) && value.ValueKind == JsonValueKind.True;
        return new CodexModelOption(id, name, defaultEffort, efforts.AsReadOnly(), isDefault);
    }

    private static string ReadRequiredString(JsonElement element, string name) =>
        ReadOptionalString(element, name) ?? throw new InvalidOperationException(IncompatibleMessage);

    private static string? ReadOptionalString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException(IncompatibleMessage);
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException(IncompatibleMessage);
        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

}
