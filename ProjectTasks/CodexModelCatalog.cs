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
    bool IsDefault = false,
    bool SupportsUltraFast = false,
    string? FastServiceTier = null)
{
    public bool SupportsFast => FastServiceTier is not null;
}

/// <summary>Explicit model discovery through an owned App Server connection; never dispatches work.</summary>
public sealed class CodexModelCatalog
{
    private const int MaximumPages = 20;
    private const string IncompatibleMessage =
        "Codex returned an unsupported model catalog. Update Codex, then refresh models.";

    public Task<IReadOnlyList<CodexModelOption>> LoadAsync(CancellationToken cancellationToken = default) =>
        LoadAsync(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), cancellationToken);

    public async Task<IReadOnlyList<CodexModelOption>> LoadAsync(string folder,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await using var connection = await CodexAppServerConnection.StartAsync(
                folder, timeout.Token).ConfigureAwait(false);
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
        var speedRequirements = await ReadSpeedRequirementsAsync(connection, token).ConfigureAwait(false);
        var models = new List<CodexModelOption>();
        var modelIds = new HashSet<string>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        for (var page = 0; page < MaximumPages; page++)
        {
            // Older v2 catalogs declare only cursor/limit. Current catalogs hide
            // picker-hidden models by default; retain that default on both versions.
            var result = await connection.RequestAsync("model/list",
                new { limit = 100, cursor }, token).ConfigureAwait(false);
            if (!result.TryGetProperty("data", out var entries) || entries.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException(IncompatibleMessage);
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) throw new InvalidOperationException(IncompatibleMessage);
                if (entry.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True) continue;
                var model = ParseModel(entry, speedRequirements);
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
    private static CodexModelOption ParseModel(JsonElement entry, (bool Fast, bool UltraFast) speedRequirements)
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
        var tiers = ReadServiceTiers(entry);
        var fastTier = !speedRequirements.Fast ? null : tiers.Contains("priority") ? "priority" : tiers.Contains("fast") ? "fast" : null;
        return new CodexModelOption(id, name, defaultEffort, efforts.AsReadOnly(), isDefault,
            speedRequirements.UltraFast && tiers.Contains("ultrafast"), fastTier);
    }

    private static async Task<(bool Fast, bool UltraFast)> ReadSpeedRequirementsAsync(
        CodexAppServerConnection connection, CancellationToken token)
    {
        JsonElement response;
        try
        {
            response = await connection.RequestAsync("configRequirements/read", new { }, token).ConfigureAwait(false);
        }
        catch (CodexRequestException ex) when (ex.IsMethodUnavailable)
        {
            // Older Fast catalogs remain usable. Ultrafast requires a successful
            // requirements read before this client can expose its higher usage.
            return (true, false);
        }
        if (response.ValueKind != JsonValueKind.Object
            || !response.TryGetProperty("requirements", out var requirements))
            throw new InvalidOperationException(IncompatibleMessage);
        var independentModes = false;
        if (response.TryGetProperty("supportsIndependentSpeedModes", out var independent)
            && independent.ValueKind != JsonValueKind.Null)
        {
            if (independent.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidOperationException(IncompatibleMessage);
            independentModes = independent.GetBoolean();
        }
        if (requirements.ValueKind == JsonValueKind.Null) return (true, true);
        if (requirements.ValueKind != JsonValueKind.Object) throw new InvalidOperationException(IncompatibleMessage);
        if (!requirements.TryGetProperty("featureRequirements", out var features) || features.ValueKind == JsonValueKind.Null)
            return (true, true);
        if (features.ValueKind != JsonValueKind.Object) throw new InvalidOperationException(IncompatibleMessage);
        var fastAllowed = ReadOptionalFeatureRequirement(features, "fast_mode") != false;
        var ultraAllowed = ReadOptionalFeatureRequirement(features, "ultrafast_mode") != false;
        // Match the native app: older servers use Fast as the shared speed gate;
        // independent-mode servers can allow Ultrafast while Fast is disabled.
        return (fastAllowed, ultraAllowed && (independentModes || fastAllowed));
    }

    private static bool? ReadOptionalFeatureRequirement(JsonElement features, string name)
    {
        if (!features.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidOperationException(IncompatibleMessage);
        return value.GetBoolean();
    }

    private static HashSet<string> ReadServiceTiers(JsonElement entry)
    {
        var tiers = new HashSet<string>(StringComparer.Ordinal);
        if (entry.TryGetProperty("serviceTiers", out var offered))
        {
            if (offered.ValueKind != JsonValueKind.Array) throw new InvalidOperationException(IncompatibleMessage);
            foreach (var tier in offered.EnumerateArray()) tiers.Add(ReadRequiredString(tier, "id"));
            // A modern catalog's explicit empty list is authoritative. Never
            // restore a tier that it removed by consulting deprecated metadata.
            return tiers;
        }
        if (entry.TryGetProperty("additionalSpeedTiers", out var legacy))
        {
            if (legacy.ValueKind != JsonValueKind.Array) throw new InvalidOperationException(IncompatibleMessage);
            foreach (var tier in legacy.EnumerateArray())
            {
                if (tier.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(tier.GetString()))
                    throw new InvalidOperationException(IncompatibleMessage);
                tiers.Add(tier.GetString()!);
            }
        }
        return tiers;
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
