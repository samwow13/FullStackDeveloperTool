using System.Data.Common;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FullStackLauncher.Models;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FullStackLauncher.Services;

/// <summary>
/// An explicit, in-memory review of a Local API's standard connection settings.
/// Writes only the existing Windows-user encrypted API store, never API source or legacy secrets.
/// </summary>
public sealed class ApiDatabaseConfiguration
{
    private const int MaximumFileBytes = 4 * 1024 * 1024;
    private readonly ServiceProfile _profile;
    private readonly string _profileId;
    private readonly string _profileDirectory;
    private readonly string _startCommand;
    private readonly string _url;
    private readonly string? _selection;
    private readonly string? _launchCommand;
    private readonly ApiSecretStore _store;
    private readonly int? _npgsqlMajorVersion;
    private readonly Dictionary<string, string?> _fileRevisions = new(StringComparer.OrdinalIgnoreCase);
    private ApiSecretSnapshot _snapshot;
    private bool _hasSaved;

    private ApiDatabaseConfiguration(ServiceProfile profile, string directory, bool importLegacyForReview)
    {
        if (profile.IsConsole)
            throw new ApiSecretStoreException("Choose a web API service before changing its database.");
        if (profile.ApiConfiguration?.Environment is not (null or "Local"))
            throw new ApiSecretStoreException("Change DB supports Local API configuration only. Select Local before changing its database.");
        if (!Uri.TryCreate(profile.Url, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("http" or "https") || !endpoint.IsLoopback ||
            !string.IsNullOrEmpty(endpoint.UserInfo))
            throw new ApiSecretStoreException("Set a loopback HTTP or HTTPS API URL before changing its database.");

        _profile = profile;
        _profileId = profile.Id;
        _profileDirectory = profile.WorkingDirectory;
        _startCommand = profile.StartCommand;
        _url = profile.Url;
        _selection = profile.ApiConfiguration?.Environment;
        _launchCommand = profile.ApiConfiguration?.LaunchCommand;
        WorkingDirectory = Path.GetFullPath(directory);
        _store = new ApiSecretStore(WorkingDirectory);
        ProjectFilePath = _store.ProjectFilePath;
        ObserveRevision(ProjectFilePath);
        _npgsqlMajorVersion = DatabaseConnectionSecurity.ReadNpgsqlMajorVersion(ProjectFilePath);
        _snapshot = _store.Load(production: false);

        if (!_snapshot.HasProtectedStore && _store.HasUserSecretsReference)
        {
            // Track even a missing legacy file so a new external store cannot silently appear
            // between review and the first protected save. A protected store remains authoritative.
            var legacyPath = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "Microsoft", "UserSecrets", _store.UserSecretsId, "secrets.json");
            ObserveRevision(legacyPath);
            if (_snapshot.HasLegacyStore)
            {
                if (!importLegacyForReview)
                    throw new ApiSecretStoreException("Review and import this Local profile's existing .NET user-secrets before changing its database. Original plaintext files remain unchanged.");
                _snapshot = _store.ReadLegacyForImport(production: false, _snapshot);
            }
        }

        if (_selection is null) ValidateLegacyLaunch(endpoint);
        ValidateStartupConfiguration();
        var connections = new Dictionary<string, ApiDatabaseConnectionSetting>(StringComparer.OrdinalIgnoreCase);
        ReadAppSettings("appsettings.json", connections);
        ReadAppSettings("appsettings.Development.json", connections);
        foreach (var pair in _snapshot.Values.Where(pair => IsConnectionKey(pair.Key)))
        {
            connections[pair.Key] = new(pair.Key, pair.Value,
                _snapshot.IsLegacyImport ? "Reviewed Local .NET user-secrets" : "Encrypted Local launcher override",
                ReadDatabaseName(pair.Value));
        }
        if (connections.Count == 0)
            throw new ApiSecretStoreException("No DefaultConnectionString or standard ConnectionStrings setting was found in this API's Local configuration. Add one in API configuration, then reopen Change DB.");
        Connections = Array.AsReadOnly(connections.Values.OrderBy(setting => ApiDatabaseIdentifier.ConnectionKeyPriority(setting.Key))
            .ThenBy(setting => setting.Key, StringComparer.OrdinalIgnoreCase).ToArray());
        ValidateSnapshot();
    }

    public string WorkingDirectory { get; }
    public string ProjectFilePath { get; }
    public string Environment => "Local";
    public IReadOnlyList<ApiDatabaseConnectionSetting> Connections { get; private set; }
    public bool HasStagedLegacyValues => _snapshot.IsLegacyImport;
    public bool HasLegacyStore => _snapshot.HasLegacyStore;

    public static ApiDatabaseConfiguration Load(ServiceProfile profile, string directory, string environment = "Local",
        bool importLegacyForReview = false)
    {
        if (environment != "Local")
            throw new ApiSecretStoreException("Change DB supports Local API configuration only. Production configuration must be reviewed in API configuration.");
        try { return new(profile, directory, importLegacyForReview); }
        catch (Exception ex) when (IsFileError(ex))
        {
            throw new ApiSecretStoreException("The API's Local database configuration could not be read safely. Check its project folder, configuration files and access permissions.");
        }
    }

    /// <summary>Validates an edited value and source snapshots without writing or connecting.</summary>
    public ApiDatabaseConnectionChange Prepare(string key, string connectionString)
    {
        ValidateSnapshot();
        if (!Connections.Any(setting => setting.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
            throw new ApiSecretStoreException("Choose one of the discovered connection settings before saving.");
        ApiSecretStore.ValidateKey(key);
        if (string.IsNullOrWhiteSpace(connectionString) || connectionString.Length > MaximumFileBytes ||
            connectionString.Any(character => char.IsControl(character)))
            throw new ApiSecretStoreException("Enter a nonempty connection string without control characters.");

        string normalized;
        try
        {
            _ = new DbConnectionStringBuilder { ConnectionString = connectionString };
            normalized = DatabaseConnectionSecurity.NormalizeApiConnectionString(connectionString, _npgsqlMajorVersion);
        }
        catch (ArgumentException)
        {
            throw new ApiSecretStoreException("The connection string is invalid or uses unsupported PostgreSQL security options. Check its keywords and quoting; remote PostgreSQL requires SSL Mode=VerifyFull and a trusted certificate.");
        }
        var databaseName = ReadDatabaseName(normalized);
        if (databaseName is null)
            throw new ApiSecretStoreException("Use exactly one Database or Initial Catalog setting with a database name of at most 64 letters, digits, spaces, dots, underscores or hyphens, beginning with a letter or digit.");

        // The restart consumes the complete protected profile. Check every unchanged entry
        // before saving so a malformed unrelated override cannot turn Save into a failed preflight.
        if (_snapshot.NullKeys.Any(other => !other.Equals(key, StringComparison.OrdinalIgnoreCase)))
            throw new ApiSecretStoreException("This Local profile contains other JSON null or container overrides that cannot be passed to the API. Repair them in API configuration before changing its database.");
        foreach (var pair in _snapshot.Values)
        {
            ApiSecretStore.ValidateKey(pair.Key);
            if (pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            if (pair.Value.Contains('\0'))
                throw new ApiSecretStoreException("This Local profile contains a value that cannot be passed to the API. Repair it in API configuration before changing its database.");
            if (ApiDatabaseIdentifier.IsConnectionKey(pair.Key))
            {
                try { _ = DatabaseConnectionSecurity.NormalizeApiConnectionString(pair.Value, _npgsqlMajorVersion); }
                catch (ArgumentException)
                {
                    throw new ApiSecretStoreException("Another Local connection override is invalid or uses unsupported PostgreSQL security options. Repair it in API configuration before changing its database.");
                }
            }
        }
        return new(this, key, normalized, databaseName);
    }

    /// <summary>Preserves unrelated Local overrides and commits only encrypted API configuration.</summary>
    public void Save(ApiDatabaseConnectionChange change)
    {
        if (change is null || !ReferenceEquals(change.Owner, this))
            throw new ApiSecretStoreException("This database draft belongs to a different API configuration. Reopen Change DB before saving.");
        // Revalidate after any asynchronous UI preflight. ApiSecretStore also rechecks its
        // ciphertext revision while holding the existing writer mutex and cross-session lock.
        var current = Prepare(change.Key, change.ConnectionString);
        var values = _snapshot.Values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        values[current.Key] = current.ConnectionString;
        var reviewed = Array.AsReadOnly(Connections.Select(setting => values.TryGetValue(setting.Key, out var value)
            ? new ApiDatabaseConnectionSetting(setting.Key, value, "Encrypted Local launcher override", ReadDatabaseName(value))
            : setting).ToArray());
        _snapshot = _store.Save(production: false, _snapshot, values,
            new HashSet<string>([current.Key], StringComparer.OrdinalIgnoreCase));
        Connections = reviewed;
        _hasSaved = true;
    }

    /// <summary>Captures a managed Local launch matching this exact saved change.</summary>
    public ApiLaunchConfiguration PrepareLaunch(ApiDatabaseConnectionChange change)
    {
        if (change is null || !ReferenceEquals(change.Owner, this) || !_snapshot.HasProtectedStore ||
            !_snapshot.Values.TryGetValue(change.Key, out var saved) || saved != change.ConnectionString)
            throw new ApiSecretStoreException("Save this reviewed database change before preparing its API restart.");
        ValidateSnapshot();
        var launch = ApiLaunchConfiguration.Prepare(_profile, WorkingDirectory, "Local");
        ValidateSnapshot();
        foreach (var pair in _snapshot.Values)
        {
            var expected = ApiDatabaseIdentifier.IsConnectionKey(pair.Key)
                ? DatabaseConnectionSecurity.NormalizeApiConnectionString(pair.Value, _npgsqlMajorVersion) : pair.Value;
            if (!launch.StartInfo.Environment.TryGetValue(pair.Key.Replace(":", "__", StringComparison.Ordinal), out var actual) ||
                actual != expected)
                throw Changed();
        }
        return launch;
    }

    public void ValidateSnapshot()
    {
        try
        {
            if (_profile.IsConsole || _profile.Id != _profileId || _profile.WorkingDirectory != _profileDirectory ||
                _profile.StartCommand != _startCommand || _profile.Url != _url ||
                _profile.ApiConfiguration?.LaunchCommand != _launchCommand ||
                (_profile.ApiConfiguration?.Environment != _selection &&
                 !(_hasSaved && _selection is null && _profile.ApiConfiguration?.Environment == "Local")))
                throw Changed();
            foreach (var pair in _fileRevisions)
            {
                var bytes = ReadFile(pair.Key);
                try { if (!string.Equals(Revision(bytes), pair.Value, StringComparison.Ordinal)) throw Changed(); }
                finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
            }
            var latest = _store.Load(production: false);
            if (latest.Revision != _snapshot.Revision || latest.HasProtectedStore != _snapshot.HasProtectedStore)
                throw Changed();
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            throw new ApiSecretStoreException("The API configuration could not be rechecked. Files were preserved; reload Change DB before saving.", isConflict: true);
        }
    }

    public static string? ReadDatabaseName(string connectionString)
    {
        try
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
            var keys = DatabaseKeys(builder);
            return keys.Length == 1 ? ApiDatabaseIdentifier.SafeName(builder[keys[0]]?.ToString()) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException) { return null; }
    }

    public static string WithDatabaseName(string connectionString, string databaseName)
    {
        var name = ApiDatabaseIdentifier.SafeName(databaseName);
        if (name is null)
            throw new ApiSecretStoreException("Enter a database name of at most 64 letters, digits, spaces, dots, underscores or hyphens, beginning with a letter or digit.");
        try
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
            var keys = DatabaseKeys(builder);
            if (keys.Length != 1)
                throw new ApiSecretStoreException("The connection must contain exactly one Database or Initial Catalog setting before its database name can be changed.");
            builder[keys[0]] = name;
            return builder.ConnectionString;
        }
        catch (ArgumentException)
        {
            throw new ApiSecretStoreException("The connection string could not be parsed. Correct its keywords and quoting before changing its database name.");
        }
    }

    private static string[] DatabaseKeys(DbConnectionStringBuilder builder) => builder.Keys.Cast<string>().Where(key =>
        key.Equals("Database", StringComparison.OrdinalIgnoreCase) || key.Equals("Initial Catalog", StringComparison.OrdinalIgnoreCase)).ToArray();

    private static bool IsConnectionKey(string key) => ApiDatabaseIdentifier.IsConnectionKey(key) &&
        (key.Equals(ApiDatabaseIdentifier.DefaultConnectionStringKey, StringComparison.OrdinalIgnoreCase) ||
         key.Length > ApiDatabaseIdentifier.ConnectionPrefix.Length && !key[ApiDatabaseIdentifier.ConnectionPrefix.Length..].Contains(':'));

    private void ReadAppSettings(string fileName, Dictionary<string, ApiDatabaseConnectionSetting> connections)
    {
        var content = ObserveFile(Path.Combine(WorkingDirectory, fileName));
        if (content is null) return;
        try
        {
            using var document = JsonDocument.Parse(Decode(content), new JsonDocumentOptions
            { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 64 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw InvalidJson();
            var leaves = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Visit("", document.RootElement);

            void Visit(string prefix, JsonElement value)
            {
                if (IsConnectionKey(prefix) && value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    throw InvalidJson();
                if (value.ValueKind == JsonValueKind.Object)
                {
                    var properties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var property in value.EnumerateObject())
                    {
                        if (!properties.Add(property.Name)) throw InvalidJson();
                        Visit(prefix.Length == 0 ? property.Name : prefix + ":" + property.Name, property.Value);
                    }
                    return;
                }
                if (value.ValueKind == JsonValueKind.Array)
                {
                    var index = 0;
                    foreach (var item in value.EnumerateArray()) Visit(prefix + ":" + index++, item);
                    return;
                }
                if (!leaves.Add(prefix)) throw InvalidJson();
                if (!IsConnectionKey(prefix)) return;
                if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw InvalidJson();
                var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
                connections[prefix] = new(prefix, text, fileName, ReadDatabaseName(text));
            }
        }
        catch (JsonException) { throw InvalidJson(); }
        finally { CryptographicOperations.ZeroMemory(content); }
    }

    private void ValidateStartupConfiguration()
    {
        var foundStandardBuilder = false;
        foreach (var file in new[] { "Program.cs", "Startup.cs" })
        {
            var content = ObserveFile(Path.Combine(WorkingDirectory, file));
            if (content is null) continue;
            try
            {
                var root = CSharpSyntaxTree.ParseText(Decode(content)).GetRoot();
                foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var method = invocation.Expression switch
                    {
                        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                        _ => ""
                    };
                    if (method is "CreateBuilder" or "CreateDefaultBuilder")
                    {
                        if (invocation.Expression is MemberAccessExpressionSyntax builder &&
                            builder.Expression.ToString() is "WebApplication" or "Host" or "WebHost")
                        {
                            if (invocation.ArgumentList.Arguments.Count > 1 ||
                                invocation.ArgumentList.Arguments.Any(argument => !IsStandardArgs(argument.Expression)))
                                throw UnsupportedProviders();
                            foundStandardBuilder = true;
                        }
                    }
                    if (method is "ConfigureAppConfiguration" or "AddJsonFile" or "AddJsonStream" or "AddUserSecrets" or
                        "AddEnvironmentVariables" or "AddCommandLine" or "AddAzureKeyVault" or "AddAzureAppConfiguration" or
                        "AddInMemoryCollection" or "AddIniFile" or "AddXmlFile" or "AddKeyPerFile" or "SetBasePath")
                        throw UnsupportedProviders();
                    if (invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                        IsConfigurationReceiver(memberAccess.Expression) &&
                        (method.StartsWith("Add", StringComparison.Ordinal) || method is "Clear" or "Insert" or "Remove" or "RemoveAt"))
                        throw UnsupportedProviders();
                }
                if (root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
                    .Any(creation => creation.Type.ToString().EndsWith("ConfigurationBuilder", StringComparison.Ordinal)))
                    throw UnsupportedProviders();
                if (root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                    .Any(assignment => assignment.Left is ElementAccessExpressionSyntax element && IsConfigurationReceiver(element.Expression) ||
                        assignment.Left is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Sources" } member && IsConfigurationReceiver(member.Expression)))
                    throw UnsupportedProviders();
            }
            finally { CryptographicOperations.ZeroMemory(content); }
        }
        if (!foundStandardBuilder) throw UnsupportedProviders();
    }

    private static bool IsStandardArgs(ExpressionSyntax expression)
    {
        if (expression is IdentifierNameSyntax { Identifier.ValueText: "args" }) return true;
        if (expression is ParenthesizedExpressionSyntax parenthesized) return IsStandardArgs(parenthesized.Expression);
        if (expression is not InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member } invocation ||
            !IsStandardArgs(member.Expression)) return false;
        if (member.Name.Identifier.ValueText is "ToArray" or "ToList" or "AsEnumerable")
            return invocation.ArgumentList.Arguments.Count == 0;
        if (member.Name.Identifier.ValueText != "Where" || invocation.ArgumentList.Arguments.Count != 1) return false;
        // A filter may remove application maintenance switches, but must not inject host options.
        // Recognize expression-only filters; no execution, semantic loading, or argument values.
        var predicate = invocation.ArgumentList.Arguments[0].Expression;
        var body = predicate switch
        {
            SimpleLambdaExpressionSyntax lambda => lambda.Body as ExpressionSyntax,
            ParenthesizedLambdaExpressionSyntax lambda when lambda.ParameterList.Parameters.Count == 1 => lambda.Body as ExpressionSyntax,
            _ => null
        };
        return body is not null && !body.DescendantNodesAndSelf().Any(node => node is
            InvocationExpressionSyntax or AssignmentExpressionSyntax or ObjectCreationExpressionSyntax or AwaitExpressionSyntax);
    }

    private static bool IsConfigurationReceiver(ExpressionSyntax expression)
    {
        // Inspect only the receiver chain. Configuration used as a service-registration
        // argument does not make the service builder a configuration provider.
        while (true)
        {
            switch (expression)
            {
                case IdentifierNameSyntax identifier:
                    return identifier.Identifier.ValueText.Equals("configuration", StringComparison.OrdinalIgnoreCase);
                case MemberAccessExpressionSyntax member:
                    if (member.Name.Identifier.ValueText == "Configuration") return true;
                    expression = member.Expression;
                    break;
                case InvocationExpressionSyntax invocation:
                    expression = invocation.Expression;
                    break;
                case ParenthesizedExpressionSyntax parenthesized:
                    expression = parenthesized.Expression;
                    break;
                default:
                    return false;
            }
        }
    }

    private void ValidateLegacyLaunch(Uri endpoint)
    {
        // A legacy shell run inherits configuration that a managed Local launch removes.
        // Do not claim appsettings/legacy secrets are effective when another standard
        // environment provider can override them or redirect the content root.
        foreach (System.Collections.DictionaryEntry variable in System.Environment.GetEnvironmentVariables())
        {
            var key = variable.Key as string ?? "";
            if (key.Contains(':') || key.Contains("__", StringComparison.Ordinal) || ApiDatabaseIdentifier.IsConnectionKey(key) ||
                new[] { "CUSTOMCONNSTR_", "SQLCONNSTR_", "SQLAZURECONNSTR_", "MYSQLCONNSTR_" }
                    .Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ||
                new[] { "ASPNETCORE_CONTENTROOT", "DOTNET_CONTENTROOT", "ASPNETCORE_APPLICATIONNAME", "DOTNET_APPLICATIONNAME" }
                    .Contains(key, StringComparer.OrdinalIgnoreCase))
                throw UnsupportedCommand();
            if (key.Equals("ASPNETCORE_ENVIRONMENT", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("DOTNET_ENVIRONMENT", StringComparison.OrdinalIgnoreCase))
            {
                if (variable.Value is not string value || value != "Development") throw UnsupportedCommand();
            }
        }
        var tokens = Tokenize(_startCommand);
        var index = tokens.Count > 0 && tokens[0].Equals("call", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        if (tokens.Count < index + 2 || !tokens[index].Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
            !tokens[index + 1].Equals("run", StringComparison.OrdinalIgnoreCase)) throw UnsupportedCommand();
        var noLaunchProfile = false;
        for (index += 2; index < tokens.Count; index++)
        {
            var argument = tokens[index];
            if (argument == "--") continue;
            if (argument == "--no-launch-profile") { noLaunchProfile = true; continue; }
            var equals = argument.IndexOf('=');
            var option = equals < 0 ? argument : argument[..equals];
            if (option is not ("--project" or "--urls")) throw UnsupportedCommand();
            var value = equals < 0 ? (++index < tokens.Count ? tokens[index] : "") : argument[(equals + 1)..];
            if (value.Length == 0) throw UnsupportedCommand();
            if (option == "--project")
            {
                var path = Path.GetFullPath(Path.Combine(WorkingDirectory, value));
                if (Directory.Exists(path)) path = Path.Combine(path, Path.GetFileName(ProjectFilePath));
                if (!path.Equals(ProjectFilePath, StringComparison.OrdinalIgnoreCase)) throw UnsupportedCommand();
            }
            else if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.GetLeftPart(UriPartial.Authority) != endpoint.GetLeftPart(UriPartial.Authority))
                throw UnsupportedCommand();
        }
        if (noLaunchProfile) return;
        var content = ObserveFile(Path.Combine(WorkingDirectory, "Properties", "launchSettings.json"));
        if (content is null) return;
        try
        {
            using var document = JsonDocument.Parse(Decode(content), new JsonDocumentOptions
            { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 64 });
            if (!document.RootElement.TryGetProperty("profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Object)
                throw UnsupportedCommand();
            // dotnet run chooses the first Project profile when none is explicitly named.
            var projectProfile = profiles.EnumerateObject().Select(property => property.Value).FirstOrDefault(value =>
                value.ValueKind == JsonValueKind.Object && value.TryGetProperty("commandName", out var name) && name.GetString() == "Project");
            if (projectProfile.ValueKind == JsonValueKind.Undefined) throw UnsupportedCommand();
            if (projectProfile.TryGetProperty("commandLineArgs", out var args) && !string.IsNullOrWhiteSpace(args.GetString())) throw UnsupportedCommand();
            if (projectProfile.TryGetProperty("workingDirectory", out var working) && !string.IsNullOrWhiteSpace(working.GetString())) throw UnsupportedCommand();
            if (projectProfile.TryGetProperty("environmentVariables", out var variables))
            {
                if (variables.ValueKind != JsonValueKind.Object) throw UnsupportedCommand();
                foreach (var variable in variables.EnumerateObject())
                {
                    if (variable.Value.ValueKind != JsonValueKind.String) throw UnsupportedCommand();
                    if (variable.Name is "ASPNETCORE_ENVIRONMENT" or "DOTNET_ENVIRONMENT")
                    {
                        if (variable.Value.GetString() != "Development") throw UnsupportedCommand();
                    }
                    else if (variable.Name != "ASPNETCORE_URLS") throw UnsupportedCommand();
                }
            }
        }
        catch (JsonException) { throw UnsupportedCommand(); }
        catch (InvalidOperationException) { throw UnsupportedCommand(); }
        finally { CryptographicOperations.ZeroMemory(content); }
    }

    private static List<string> Tokenize(string command)
    {
        if (command.IndexOfAny(['&', '|', '<', '>', '^', '%', '\r', '\n', '\0']) >= 0) throw UnsupportedCommand();
        var tokens = new List<string>();
        var index = 0;
        while (index < command.Length)
        {
            while (index < command.Length && char.IsWhiteSpace(command[index])) index++;
            if (index == command.Length) break;
            var value = new StringBuilder();
            var quoted = false;
            while (index < command.Length && (quoted || !char.IsWhiteSpace(command[index])))
            {
                var character = command[index++];
                if (character == '"') quoted = !quoted;
                else value.Append(character);
            }
            if (quoted) throw UnsupportedCommand();
            tokens.Add(value.ToString());
        }
        return tokens;
    }

    private byte[]? ObserveFile(string path)
    {
        var content = ReadFile(path);
        _fileRevisions.Add(path, Revision(content));
        return content;
    }

    private void ObserveRevision(string path)
    {
        var content = ObserveFile(path);
        if (content is not null) CryptographicOperations.ZeroMemory(content);
    }

    private static byte[]? ReadFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaximumFileBytes) throw new ApiSecretStoreException("An API configuration file is too large to review safely.");
            using var content = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = stream.Read(buffer)) > 0)
            {
                if (content.Length + read > MaximumFileBytes) throw new ApiSecretStoreException("An API configuration file is too large to review safely.");
                content.Write(buffer, 0, read);
            }
            return content.ToArray();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static string Decode(byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string? Revision(byte[]? bytes) => bytes is null ? null : Convert.ToHexString(SHA256.HashData(bytes));
    private static ApiSecretStoreException Changed() => new("The API project, launch settings or Local configuration changed after review. Reload Change DB and review the latest values before saving.", isConflict: true);
    private static ApiSecretStoreException InvalidJson() => new("The API appsettings files contain invalid or ambiguous connection settings. Repair their JSON before reopening Change DB.");
    private static ApiSecretStoreException UnsupportedProviders() => new("This API's startup configuration is not supported by Change DB. Use API configuration and review its custom configuration providers before restarting.");
    private static ApiSecretStoreException UnsupportedCommand() => new("This API uses launch arguments or a launch profile that Change DB cannot preserve safely. Review its command and enable managed Local API configuration before changing its database.");
    private static bool IsFileError(Exception exception) => exception is IOException or UnauthorizedAccessException or
        System.Security.SecurityException or ArgumentException or NotSupportedException or DecoderFallbackException;
}

/// <summary>Secret text is retained only in the modal's in-memory review.</summary>
public sealed class ApiDatabaseConnectionSetting
{
    internal ApiDatabaseConnectionSetting(string key, string value, string source, string? databaseName)
    { Key = key; Value = value; Source = source; DatabaseName = databaseName; }
    public string Key { get; }
    public string Value { get; }
    public string Source { get; }
    public string? DatabaseName { get; }
    public override string ToString() => Key;
}

/// <summary>A validated in-memory change; no secret is exposed by default formatting.</summary>
public sealed class ApiDatabaseConnectionChange
{
    internal ApiDatabaseConnectionChange(ApiDatabaseConfiguration owner, string key, string connectionString, string databaseName)
    { Owner = owner; Key = key; ConnectionString = connectionString; DatabaseName = databaseName; }
    internal ApiDatabaseConfiguration Owner { get; }
    public string Key { get; }
    public string ConnectionString { get; }
    public string DatabaseName { get; }
    public override string ToString() => "Local database configuration change";
}
