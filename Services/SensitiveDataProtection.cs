using System.Text.RegularExpressions;

namespace FullStackLauncher.Services;

/// <summary>Defense in depth for recognizable credentials; arbitrary program output can still contain secrets.</summary>
public static class SensitiveDataProtection
{
    private const string NamedPrefix = """(?ix)(?<prefix>(?<![\w])(?:[\w.-]*(?:__|:))?(?:password|passwd|pwd|psw|pgpassword|token|access[ _-]?token|refresh[ _-]?token|api[ _-]?key|client[ _-]?secret|signing[ _-]?key|account[ _-]?key|shared[ _-]?access[ _-]?key|secret|authorization|ssl[ _-]?password)(?:["'])?\s*(?:=|:|\s)\s*)""";
    private static readonly Regex NamedValue = new(
        NamedPrefix + """(?<value>"(?:\\.|""|[^"\\])*"|'(?:\\.|''|[^'\\])*'|[^\s;,"'&|<>]+)""",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex LoggedNamedValue = new(
        NamedPrefix + """(?<value>"(?:\\.|""|[^"\\])*"|'(?:\\.|''|[^'\\])*'|[^\r\n;,"'&|<>]+)""",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex CredentialUrl = new(
        """(?i)(?<prefix>\b[a-z][a-z0-9+.-]*://)(?<value>[^/\s@]+@)""",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Bearer = new(
        """(?i)(?<prefix>\b(?:Bearer|Basic)\s+)(?<value>[a-z0-9+/_=.-]+)""",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex KnownToken = new(
        """(?x)\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-(?:proj-)?[A-Za-z0-9_-]{20,}|(?:sk|rk)_(?:live|test)_[A-Za-z0-9]{16,}|xox[baprs]-[A-Za-z0-9-]{16,}|eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+)\b""",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex EnvironmentReference = new(
        """^(?:%[A-Za-z_][A-Za-z0-9_]*%|\$(?:env:)?[A-Za-z_][A-Za-z0-9_]*|\$\{[A-Za-z_][A-Za-z0-9_]*\})$""",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static bool ContainsLiteralCredential(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        try
        {
            if (CredentialUrl.IsMatch(text) || Bearer.IsMatch(text) || KnownToken.IsMatch(text)) return true;
            return NamedValue.Matches(text).Any(match =>
            {
                var value = match.Groups["value"].Value.Trim('"', '\'');
                return value.Length > 0 && !EnvironmentReference.IsMatch(value);
            });
        }
        catch (RegexMatchTimeoutException) { return true; }
    }

    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        try
        {
            var result = CredentialUrl.Replace(text, "${prefix}[redacted]@");
            result = Bearer.Replace(result, "${prefix}[redacted]");
            // Unquoted connection-string passwords may contain spaces; withhold through the next
            // delimiter rather than exposing a password suffix. Logs can lose nonsecret context.
            result = LoggedNamedValue.Replace(result, "${prefix}[redacted]");
            return KnownToken.Replace(result, "[redacted]");
        }
        catch (RegexMatchTimeoutException) { return "[output withheld: credential filtering limit reached]"; }
    }
}
