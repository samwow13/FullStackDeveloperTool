using System.Data.Common;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Npgsql;

namespace FullStackLauncher.Services;

/// <summary>Remote PostgreSQL connections must authenticate their server, including its hostname.</summary>
public static class DatabaseConnectionSecurity
{
    public const string RemoteTlsNotice = "Remote PostgreSQL connections use SSL Mode=VerifyFull and validate the server certificate and hostname. Install the server's trusted CA or set Root Certificate if needed; certificate checks cannot be disabled.";

    public static string NormalizePostgresConnectionString(string connectionString, bool useNpgsql10 = true)
    {
        try
        {
            // Remove the obsolete trust bypass before Npgsql parsing, including compact aliases.
            var values = new DbConnectionStringBuilder { ConnectionString = connectionString };
            foreach (var key in values.Keys.Cast<string>().Where(key => Compact(key) == "trustservercertificate").ToArray())
                values.Remove(key);
            var builder = new NpgsqlConnectionStringBuilder(values.ConnectionString);
            if (string.IsNullOrWhiteSpace(builder.Host) || builder.Host.Any(char.IsControl))
                throw new ArgumentException();
            if (!IsLoopbackOnly(builder.Host))
            {
                builder.SslMode = SslMode.VerifyFull;
                // Npgsql 10 prefers GSS over SSL. Disable it so VerifyFull is the actual transport policy.
                if (useNpgsql10 || values.Keys.Cast<string>().Any(key => Compact(key) == "gssencryptionmode"))
                    builder.GssEncryptionMode = GssEncryptionMode.Disable;
            }
            builder.IncludeErrorDetail = false;
            builder.PersistSecurityInfo = false;
            builder.LogParameters = false;
            return builder.ConnectionString;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or FormatException or OverflowException)
        {
            throw new ArgumentException("The PostgreSQL connection is invalid or uses unsupported security options. Use an explicit Host and SSL Mode=VerifyFull for remote servers. Configure a trusted Root Certificate when required.");
        }
    }

    public static bool IsLoopbackOnly(string hosts) => hosts.Split(',').All(IsLoopbackHost);

    private static bool IsLoopbackHost(string value)
    {
        var host = value.Trim();
        if (host.Length == 0) return false;
        if (host.StartsWith('['))
        {
            var end = host.IndexOf(']');
            if (end < 0 || (end + 1 < host.Length && !ValidPort(host[(end + 1)..]))) return false;
            host = host[1..end];
        }
        else if (host.Count(character => character == ':') == 1)
        {
            var colon = host.LastIndexOf(':');
            if (!ValidPort(host[colon..])) return false;
            host = host[..colon];
        }
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(host, out var address)) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return IPAddress.IsLoopback(address);
    }

    private static bool ValidPort(string suffix) => suffix.StartsWith(':') &&
        int.TryParse(suffix[1..], out var port) && port is > 0 and <= 65535;

    // Do not interpret arbitrary SQL Server, SQLite, or other provider strings as PostgreSQL.
    // A project package reference supplies the provider context; Npgsql still validates each value.
    public static int? ReadNpgsqlMajorVersion(string projectFilePath)
    {
        try
        {
            using var reader = XmlReader.Create(projectFilePath, new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
            var packages = XDocument.Load(reader).Descendants().Where(element => element.Name.LocalName == "PackageReference")
                .Where(element => ((string?)element.Attribute("Include") ?? (string?)element.Attribute("Update") ?? "")
                    .StartsWith("Npgsql", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (packages.Length == 0) return null;
            var major = packages.Select(element => (string?)element.Attribute("Version")
                    ?? element.Elements().FirstOrDefault(child => child.Name.LocalName == "Version")?.Value)
                .Select(version => int.TryParse(version?.Split('.')[0], out var number) ? number : 0).Max();
            return major;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException) { return null; }
    }

    public static string NormalizeApiConnectionString(string value, int? npgsqlMajorVersion)
    {
        if (npgsqlMajorVersion is null || string.IsNullOrWhiteSpace(value)) return value;
        // A mixed-provider project can still contain a SQLite/SQL Server connection. A literal
        // PostgreSQL Host keyword opts this value in; provider-specific strings remain untouched.
        DbConnectionStringBuilder parsed;
        try { parsed = new() { ConnectionString = value }; }
        catch (ArgumentException)
        {
            if (Regex.IsMatch(value, @"(?:^|;)\s*Host\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(100)))
                throw new ArgumentException("A PostgreSQL Host connection string is malformed. Correct its Npgsql keywords and quoting before starting the API.");
            return value;
        }
        if (!parsed.Keys.Cast<string>().Any(key => key.Equals("Host", StringComparison.OrdinalIgnoreCase)))
        {
            // Npgsql's Server alias is indistinguishable from another provider when only common
            // keywords are present. Refuse to guess or silently skip PostgreSQL TLS enforcement.
            // Do not use the launcher's newer parser to decide whether old-driver Server strings
            // are PostgreSQL: obsolete trust/authentication keywords can make that parser reject them.
            if (parsed.Keys.Cast<string>().Any(key => key.Equals("Server", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("This API connection uses an ambiguous Server keyword. For PostgreSQL, use Host so the launcher can enforce verified TLS; otherwise use your provider's explicit connection format.");
            try
            {
                foreach (var key in parsed.Keys.Cast<string>().Where(key => Compact(key) == "trustservercertificate").ToArray())
                    parsed.Remove(key);
                var possiblePostgres = new NpgsqlConnectionStringBuilder(parsed.ConnectionString);
                if (!string.IsNullOrWhiteSpace(possiblePostgres.Host))
                    throw new InvalidOperationException("This API connection uses an ambiguous Server keyword. For PostgreSQL, use Host so the launcher can enforce verified TLS; otherwise use your provider's explicit connection format.");
            }
            catch (ArgumentException) { /* A provider-specific connection is outside the PostgreSQL policy. */ }
            return value;
        }
        var normalized = NormalizePostgresConnectionString(value, useNpgsql10: npgsqlMajorVersion >= 10);
        if (npgsqlMajorVersion == 0 && !IsLoopbackOnly(new NpgsqlConnectionStringBuilder(normalized).Host!))
            throw new InvalidOperationException("The API's PostgreSQL client version could not be determined. Pin a literal Npgsql package version in the API project so the launcher can safely enforce verified TLS for that client version.");
        return normalized;
    }

    private static string Compact(string key) => key.Replace(" ", "", StringComparison.Ordinal)
        .Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
}
