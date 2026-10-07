using System.Text.RegularExpressions;

namespace FullStackLauncher.ProjectTasks;

/// <summary>Readable AI prompt summaries without changing the detailed task body.</summary>
public static class AgentPromptSummary
{
    public const int MaximumCharacters = 200;
    private const string Prefix = "Summary: ";
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant);
    private static readonly Regex SentenceEnding = new(
        """(?<ending>[.!?]+)["'\u2019\u201D)\]}]*(?=\s|$)""", RegexOptions.CultureInvariant);

    /// <summary>Uses a valid existing summary, or a concise sentence from the note title.</summary>
    public static string GetSummary(string name, string prompt)
    {
        if (TryReadSummary(prompt, out var summary, out _)) return summary;
        return TitleSentence(name);
    }

    /// <summary>Places one summary first, preserving the remaining detailed prompt.</summary>
    public static string EnsureSummary(string name, string prompt, string? summary = null)
    {
        prompt ??= "";
        var supplied = NormalizeProvidedSummary(summary);
        var hasSummary = TryReadSummary(prompt, out var existing, out var body);
        var chosen = supplied ?? (hasSummary ? existing : TitleSentence(name));
        return Prefix + chosen + (body.Length == 0 ? "" : "\n\n" + body);
    }

    internal static string? NormalizeProvidedSummary(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return null;
        var text = summary.Trim();
        if (text.IndexOfAny(['\r', '\n', '\u2028', '\u2029']) >= 0)
            throw new ArgumentException("Follow-up summary must be one sentence on one line.");
        if (text.Length > MaximumCharacters)
            throw new ArgumentException($"Follow-up summary exceeds {MaximumCharacters} characters, including final punctuation.");
        text = Whitespace.Replace(text, " ");
        var endings = SentenceEnding.Matches(text);
        if (endings.Count > 1 || (endings.Count == 1 &&
            (endings[0].Index + endings[0].Length != text.Length || endings[0].Groups["ending"].Length != 1)))
            throw new ArgumentException("Follow-up summary must contain exactly one sentence.");
        if (endings.Count == 0) text += ".";
        if (text.Length > MaximumCharacters)
            throw new ArgumentException($"Follow-up summary exceeds {MaximumCharacters} characters, including final punctuation.");
        return text;
    }

    internal static string GetDetails(string prompt) =>
        TryReadSummary(prompt, out _, out var body) ? body : prompt;

    private static bool TryReadSummary(string? prompt, out string summary, out string body)
    {
        prompt ??= "";
        summary = "";
        body = prompt;
        var start = prompt.TrimStart();
        if (!start.StartsWith("Summary:", StringComparison.OrdinalIgnoreCase)) return false;
        var newline = start.IndexOfAny(['\r', '\n']);
        var firstLine = newline < 0 ? start : start[..newline];
        try
        {
            var parsed = NormalizeProvidedSummary(firstLine["Summary:".Length..]);
            if (parsed == null) return false;
            summary = parsed;
            body = newline < 0 ? "" : start[newline..].TrimStart('\r', '\n');
            return true;
        }
        catch (ArgumentException)
        {
            // Legacy or edited content remains intact; display a title fallback.
            return false;
        }
    }

    private static string TitleSentence(string? name)
    {
        var title = Whitespace.Replace(name ?? "", " ").Trim();
        if (title.Length == 0) return "Review proposed follow-up task.";
        var ending = SentenceEnding.Match(title);
        if (ending.Success)
            title = title[..ending.Index].TrimEnd();
        if (title.Length == 0) return "Review proposed follow-up task.";
        if (title.Length >= MaximumCharacters)
        {
            title = title[..(MaximumCharacters - 1)];
            var wordBoundary = title.LastIndexOf(' ');
            if (wordBoundary > 0) title = title[..wordBoundary];
        }
        return title.TrimEnd('.', '!', '?') + ".";
    }
}
