using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using FullStackLauncher.Models;

namespace FullStackLauncher.ViewModels;

/// <summary>A sanitized display line. Process configuration secrets are redacted by the producer.</summary>
public sealed class ConsoleLine
{
    private static readonly Regex TerminalEscapes = new(
        @"(?:\x1B\]|\x9D)[^\x07\x1B\x9C]*(?:\x07|\x1B\\|\x9C|$)|(?:\x1B[P^_X]|[\x90\x98\x9E\x9F])[^\x1B\x9C]*(?:\x1B\\|\x9C|$)|(?:\x1B\[|\x9B)[0-?]*[ -/]*[@-~]|\x1B[ -/]*[@-~]",
        RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Failed = Pattern(
        @"\b(?:(?:build|compilation|generation|restore|installation|command) failed|failed to|failure|fatal|panic|unhandled exception|address already in use|access denied|permission denied|cannot|could not|unable to|EADDRINUSE)\b|\b\w*Exception\b|\b[1-9]\d*\s+errors?(?:\(s\))?\b|\bexit(?:ed)?(?:\s+with)?\s+(?:exit\s+)?code\s*[:=]?\s*-?[1-9]\d*\b|(?:^|\s|\[|:)\s*(?:error|err|fail|critical)(?:\s*[:!\]]|\s+[a-z]*\d+\b)|\b(?:npm|pnpm)\s+ERR!|\[(?:ERROR|FAIL|FAILED)\]");
    private static readonly Regex Warning = Pattern(@"\bwarn(?:ing)?\s*[:!\]]|\bwarning\s+[a-z]*\d+\b|\b[1-9]\d*\s+warnings?(?:\(s\))?\b|\bdeprecated\b|\bconflict\b|\b(?:canceled|cancelled|interrupted)\b|\[(?:WARNING|WARN)\]");
    private static readonly Regex Succeeded = Pattern(@"\b(?:build succeeded|completed successfully|compiled successfully|successfully compiled|generation complete|ready in|now listening on|0\s+errors?(?:\(s\))?|no errors|zero errors)\b|\bexit(?:ed)?(?:\s+with)?\s+(?:exit\s+)?code\s*[:=]?\s*0\b|^[\s]*[✓✔√]");
    private static readonly Regex Information = Pattern(@"^\s*(?:info|information|debug|trace|notice)(?:\s*[:\]]|\b)|\bnpm notice\b");

    private static readonly Brush CommandBrush = Brush("#79DEFF");
    private static readonly Brush OutputBrush = Brush("#E0EBE8");
    private static readonly Brush SuccessBrush = Brush("#79E8B3");
    private static readonly Brush WarningBrush = Brush("#F3CD80");
    private static readonly Brush ErrorBrush = Brush("#FF929B");
    private static readonly Brush InformationBrush = Brush("#B9B1FF");

    private ConsoleLine(DateTime timestamp, string source, string message, ServiceLogKind kind, bool standardError)
    {
        Timestamp = timestamp;
        Source = Sanitize(source).Replace('\n', ' ').Replace('\t', ' ');
        Message = message;
        Kind = kind;
        IsStandardError = standardError;
    }

    public DateTime Timestamp { get; }
    public string TimestampLabel => Timestamp.ToString("HH:mm:ss");
    public string Source { get; }
    public string Message { get; }
    public ServiceLogKind Kind { get; }
    public bool IsStandardError { get; }
    public string Label => Kind switch
    {
        ServiceLogKind.Command => "COMMAND",
        ServiceLogKind.Success => "SUCCESS",
        ServiceLogKind.Warning => "WARNING",
        ServiceLogKind.Error => "ERROR",
        ServiceLogKind.Information => "INFO",
        _ => IsStandardError ? "STDERR" : "OUTPUT"
    };
    public Brush Foreground => Kind switch
    {
        ServiceLogKind.Command => CommandBrush,
        ServiceLogKind.Success => SuccessBrush,
        ServiceLogKind.Warning => WarningBrush,
        ServiceLogKind.Error => ErrorBrush,
        ServiceLogKind.Information => InformationBrush,
        _ => OutputBrush
    };
    public string PlainText => $"{TimestampLabel}  [{Label}] [{Source}] {Message}";

    public static ConsoleLine FromLog(ServiceLog log, string source)
    {
        var message = Sanitize(log.Message);
        var kind = log.Kind is { } explicitKind && explicitKind != ServiceLogKind.Output
            ? explicitKind : Classify(message);
        return new(log.Timestamp, source, message, kind, log.IsError);
    }

    public static ConsoleLine FromMessage(DateTime timestamp, string source, string message, ServiceLogKind kind) =>
        new(timestamp, source, Sanitize(message), kind, false);

    public static ConsoleLine Create(string message, string source, ServiceLogKind kind = ServiceLogKind.Information,
        DateTime? timestamp = null) => FromMessage(timestamp ?? DateTime.Now, source, message, kind);

    private static ServiceLogKind Classify(string message)
    {
        var trimmed = message.TrimStart();
        if (trimmed.StartsWith("> ", StringComparison.Ordinal) || trimmed.StartsWith("$ ", StringComparison.Ordinal))
            return ServiceLogKind.Command;
        // Severity comes from content, never the stderr pipe alone (many build tools use it for progress).
        if (Failed.IsMatch(message)) return ServiceLogKind.Error;
        if (Warning.IsMatch(message)) return ServiceLogKind.Warning;
        if (Succeeded.IsMatch(message)) return ServiceLogKind.Success;
        if (Information.IsMatch(message)) return ServiceLogKind.Information;
        return ServiceLogKind.Output;
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var clean = TerminalEscapes.Replace(value, "").Replace("\r\n", "\n").Replace('\r', '\n');
        var result = new StringBuilder(clean.Length);
        foreach (var c in clean)
        {
            if (c is '\n' or '\t' || !char.IsControl(c) && c is not ('\u202A' or '\u202B' or '\u202C' or '\u202D' or '\u202E' or '\u2066' or '\u2067' or '\u2068' or '\u2069'))
                result.Append(c);
        }
        return result.ToString();
    }

    private static Regex Pattern(string pattern) => new(pattern, RegexOptions.NonBacktracking | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static Brush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
