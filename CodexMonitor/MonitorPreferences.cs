using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FullStackLauncher.CodexMonitor;

internal sealed class MonitorPreferences
{
    public string? ProjectPath { get; set; }
    public List<string> ProjectPaths { get; set; } = [];
    // Null means that no watched-project list has been saved yet; an empty list
    // means the user deliberately stopped watching every project.
    public List<string>? WatchedProjectPaths { get; set; }
    public bool ReminderSoundEnabled { get; set; } = true;
    public bool OverlayVisible { get; set; }
    public double? OverlayLeft { get; set; }
    public double? OverlayTop { get; set; }
    public double OverlayWidth { get; set; } = 420;
    public double OverlayHeight { get; set; } = 440;
    public List<CompletedChatRecord> CompletedChats { get; set; } = [];
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }

    public void Normalize()
    {
        ProjectPaths ??= [];
        CompletedChats ??= [];

        if (WatchedProjectPaths is null)
        {
            var legacyProject = NormalizeProjectPath(ProjectPath);
            if (legacyProject is not null) WatchedProjectPaths = [legacyProject];
            return;
        }

        WatchedProjectPaths = WatchedProjectPaths
            .Select(NormalizeProjectPath)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? NormalizeProjectPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        try
        {
            // Do not discard saved folders merely because a drive is temporarily
            // unavailable. New Watch actions validate that the folder exists.
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
