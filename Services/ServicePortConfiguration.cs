using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using FullStackLauncher.Models;

namespace FullStackLauncher.Services;

/// <summary>Updates a detached settings draft; it never edits project files or starts a process.</summary>
public static class ServicePortConfiguration
{
    public static int? GetPort(ServiceProfile profile) => GetEndpoint(profile)?.Port;

    public static string? GetEditUnavailableReason(ServiceProfile profile, string workingDirectory)
    {
        if (profile.IsConsole) return "Console apps do not use a local port.";
        try
        {
            var endpoint = RequireEndpoint(profile);
            _ = RewriteStartCommand(profile, workingDirectory, endpoint.Port, endpoint);
            return null;
        }
        catch (InvalidOperationException ex) { return ex.Message; }
    }

    public static void Apply(ServiceProfile candidate, string workingDirectory, string portText)
    {
        if (!int.TryParse(portText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
            throw new InvalidOperationException("Enter a whole-number port from 1 to 65535.");

        var endpoint = RequireEndpoint(candidate);
        if (port == endpoint.Port) return;
        var updated = new UriBuilder(endpoint) { Port = port }.Uri;
        var command = RewriteStartCommand(candidate, workingDirectory, port, updated);

        // Complete validation before mutating even the detached draft. Keep the URL path,
        // query, fragment, UI path, API environment and all other service settings intact.
        candidate.Url = updated.AbsoluteUri;
        candidate.StartCommand = command;
    }

    private static Uri? GetEndpoint(ServiceProfile profile)
    {
        if (profile.IsConsole || !Uri.TryCreate(profile.Url, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !endpoint.IsLoopback)
            return null;
        return endpoint;
    }

    private static Uri RequireEndpoint(ServiceProfile profile) => GetEndpoint(profile)
        ?? throw new InvalidOperationException("Set a valid local HTTP or HTTPS URL in project settings before editing its port.");

    private static string RewriteStartCommand(ServiceProfile profile, string directory, int port, Uri endpoint)
    {
        // Opted-in API launches already pass the saved URL directly to dotnet, without
        // using StartCommand. Preserve both the legacy command and environment selection.
        if (profile.ApiConfiguration is not null) return profile.StartCommand;

        var tokens = Tokenize(profile.StartCommand);
        var start = tokens.Count > 0 && Is(tokens[0], "call") ? 1 : 0;
        if (tokens.Count <= start) throw UnsupportedCommand();

        if (Executable(tokens[start]) == "dotnet" && tokens.Count > start + 1 && Is(tokens[start + 1], "run"))
        {
            var arguments = RemoveOption(tokens.Skip(start + 2).ToList(), "--urls");
            if (!arguments.Any(token => Is(token, "--"))) arguments.Add(new("--", "--"));
            arguments.Add(new("--urls", "--urls"));
            var origin = endpoint.GetLeftPart(UriPartial.Authority);
            arguments.Add(new(origin, '"' + origin + '"'));
            return Join(tokens.Take(start + 2).Concat(arguments));
        }

        var commandStart = start;
        if (Executable(tokens[start]) == "npx") commandStart++;
        else if (Executable(tokens[start]) == "npm")
        {
            if (tokens.Count > start + 2 && Is(tokens[start + 1], "exec") && Is(tokens[start + 2], "--"))
                commandStart += 3;
            else return RewriteNpmScript(tokens, start, directory, port);
        }
        if (!IsPortAwareServer(tokens.Skip(commandStart).ToList())) throw UnsupportedCommand();
        var args = RemoveOption(tokens.Skip(commandStart + 1).ToList(), "--port", "-p");
        if (args.Any(token => Is(token, "--"))) throw UnsupportedCommand();
        args.Add(new("--port", "--port"));
        args.Add(new(port.ToString(CultureInfo.InvariantCulture), port.ToString(CultureInfo.InvariantCulture)));
        return Join(tokens.Take(commandStart + 1).Concat(args));
    }

    private static string RewriteNpmScript(List<CommandToken> tokens, int start, string directory, int port)
    {
        string script;
        int prefixLength;
        if (tokens.Count > start + 1 && Is(tokens[start + 1], "start"))
        {
            script = "start";
            prefixLength = start + 2;
        }
        else if (tokens.Count > start + 2 && (Is(tokens[start + 1], "run") || Is(tokens[start + 1], "run-script"))
                 && !tokens[start + 2].Value.StartsWith('-'))
        {
            script = tokens[start + 2].Value;
            prefixLength = start + 3;
        }
        else throw UnsupportedCommand();

        List<CommandToken> scriptTokens;
        try
        {
            using var package = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "package.json")),
                new JsonDocumentOptions { MaxDepth = 32 });
            if (package.RootElement.ValueKind != JsonValueKind.Object
                || !package.RootElement.TryGetProperty("scripts", out var scripts)
                || scripts.ValueKind != JsonValueKind.Object
                || !scripts.TryGetProperty(script, out var value) || value.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("The npm start script could not be verified in package.json. Use project settings to edit this service's command and URL together.");
            scriptTokens = Tokenize(value.GetString()!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            throw new InvalidOperationException("package.json could not be read to verify port support. Use project settings to edit this service's command and URL together.");
        }
        if (scriptTokens.Count > 0 && Executable(scriptTokens[0]) == "npx") scriptTokens.RemoveAt(0);
        if (!IsPortAwareServer(scriptTokens) || scriptTokens.Any(token => Is(token, "--"))) throw UnsupportedCommand();
        // CLI versions differ in how duplicate options are parsed. Appending a launcher
        // port must not leave an unchangeable second port embedded in the package script.
        if (scriptTokens.Any(token => Is(token, "--port") || Is(token, "-p")
            || HasInlineValue(token, "--port") || HasInlineValue(token, "-p")))
            throw new InvalidOperationException("This npm script already sets a port in package.json. Move its port option into the launcher's Start command (after --) in project settings to enable inline port editing.");

        var arguments = tokens.Skip(prefixLength).ToList();
        if (arguments.Count > 0 && !Is(arguments[0], "--")) throw UnsupportedCommand();
        if (arguments.Count > 0) arguments.RemoveAt(0);
        arguments = RemoveOption(arguments, "--port", "-p");
        if (arguments.Any(token => Is(token, "--"))) throw UnsupportedCommand();
        arguments.Insert(0, new("--", "--"));
        arguments.Add(new("--port", "--port"));
        arguments.Add(new(port.ToString(CultureInfo.InvariantCulture), port.ToString(CultureInfo.InvariantCulture)));
        return Join(tokens.Take(prefixLength).Concat(arguments));
    }

    private static bool IsPortAwareServer(IReadOnlyList<CommandToken> tokens)
    {
        if (tokens.Count == 0) return false;
        return Executable(tokens[0]) switch
        {
            "ng" => tokens.Count > 1 && Is(tokens[1], "serve"),
            "next" => tokens.Count > 1 && (Is(tokens[1], "dev") || Is(tokens[1], "start")),
            "vite" => tokens.Count == 1 || tokens[1].Value.StartsWith('-')
                || Is(tokens[1], "serve") || Is(tokens[1], "dev") || Is(tokens[1], "preview"),
            "webpack" => tokens.Count > 1 && Is(tokens[1], "serve"),
            "webpack-dev-server" => true,
            _ => false
        };
    }

    private static List<CommandToken> RemoveOption(List<CommandToken> tokens, params string[] names)
    {
        var result = new List<CommandToken>();
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (names.Any(name => HasInlineValue(token, name))) continue;
            if (names.Any(name => Is(token, name)))
            {
                if (++index == tokens.Count || tokens[index].Value.StartsWith('-'))
                    throw new InvalidOperationException("The start command has an incomplete port or URL option. Correct it in project settings first.");
                continue;
            }
            result.Add(token);
        }
        return result;
    }

    private static bool HasInlineValue(CommandToken token, string name) =>
        token.Value.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)
        || (name.Length == 2 && name[0] == '-' && name[1] != '-'
            && token.Value.StartsWith(name, StringComparison.OrdinalIgnoreCase)
            && token.Value.Length > 2 && char.IsAsciiDigit(token.Value[2]));

    private static List<CommandToken> Tokenize(string command)
    {
        // Retain original quoting. Rewriting a shell pipeline, expansion or escaped command
        // would need shell semantics, so leave those commands to the full profile editor.
        if (command.IndexOfAny(['&', '|', '<', '>', '^', '%', '\r', '\n', '\0']) >= 0)
            throw UnsupportedCommand();
        var result = new List<CommandToken>();
        var index = 0;
        while (index < command.Length)
        {
            while (index < command.Length && char.IsWhiteSpace(command[index])) index++;
            if (index == command.Length) break;
            var start = index;
            var quoted = false;
            var value = new StringBuilder();
            while (index < command.Length && (quoted || !char.IsWhiteSpace(command[index])))
            {
                var character = command[index++];
                if (character == '"') quoted = !quoted;
                else value.Append(character);
            }
            if (quoted) throw UnsupportedCommand();
            result.Add(new(value.ToString(), command[start..index]));
        }
        return result;
    }

    private static bool Is(CommandToken token, string value) => token.Value.Equals(value, StringComparison.OrdinalIgnoreCase);
    private static string Executable(CommandToken token) => Path.GetFileNameWithoutExtension(token.Value).ToLowerInvariant();
    private static string Join(IEnumerable<CommandToken> tokens) => string.Join(" ", tokens.Select(token => token.Raw));
    private static InvalidOperationException UnsupportedCommand() => new(
        "Inline port editing supports dotnet run and Angular, Vite, Next.js or webpack dev-server commands, including verified npm scripts. Use project settings to edit this service's command and URL together.");
    private sealed record CommandToken(string Value, string Raw);
}
