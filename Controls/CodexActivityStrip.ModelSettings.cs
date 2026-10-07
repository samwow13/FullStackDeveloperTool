using System.Globalization;

namespace FullStackLauncher.Controls;

public partial class CodexActivityStrip
{
    private static string FormatModelLabel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return "";
        if (!model.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase)) return model;
        var parts = model[4..].Split('-');
        return "GPT-" + parts[0] + string.Concat(parts.Skip(1).Select(part =>
            " " + CultureInfo.InvariantCulture.TextInfo.ToTitleCase(part)));
    }

    private static string FormatReasoningEffortLabel(string? effort) => effort?.ToLowerInvariant() switch
    {
        null or "" => "",
        "none" => "None",
        "minimal" => "Minimal",
        "low" => "Light",
        "medium" => "Medium",
        "high" => "High",
        "xhigh" => "Extra High",
        "max" => "Max",
        "ultra" => "Ultra",
        _ => effort
    };

    // Match Codex's dark composer badge: tertiary text, with Ultra in chart purple.
    private static string ThinkingColor(string? effort) =>
        string.Equals(effort, "ultra", StringComparison.OrdinalIgnoreCase) ? "#AD7BF9" : "#AFAFAF";
}
