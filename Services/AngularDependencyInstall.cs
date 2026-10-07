using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using FullStackLauncher.Models;

namespace FullStackLauncher.Services;

internal static class AngularDependencyInstall
{
    private static readonly Regex PackageName = new(@"^(?:@[A-Za-z0-9._-]+/)?[A-Za-z0-9._-]+$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static bool IsRequired(ServiceProfile profile, string workingDirectory)
    {
        if (!FrontendServiceSupport.IsFrontend(profile) ||
            (!profile.Kind.Trim().Equals("Angular", StringComparison.OrdinalIgnoreCase) &&
             !File.Exists(Path.Combine(workingDirectory, "angular.json")))) return false;

        using var manifest = File.OpenRead(Path.Combine(workingDirectory, "package.json"));
        if (manifest.Length > 1024 * 1024)
            throw new InvalidOperationException("Angular package.json is too large to inspect before starting.");
        using var document = JsonDocument.Parse(manifest);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Angular package.json must contain a JSON object.");

        var packages = new HashSet<string>(StringComparer.Ordinal);
        var optionalPackages = document.RootElement.TryGetProperty("optionalDependencies", out var optional) &&
            optional.ValueKind == JsonValueKind.Object
            ? optional.EnumerateObject().Select(dependency => dependency.Name).ToHashSet(StringComparer.Ordinal)
            : [];
        foreach (var section in new[] { "dependencies", "devDependencies" })
        {
            if (!document.RootElement.TryGetProperty(section, out var dependencies)) continue;
            if (dependencies.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"Angular package.json {section} must contain a JSON object.");
            foreach (var dependency in dependencies.EnumerateObject())
            {
                if (!PackageName.IsMatch(dependency.Name) || dependency.Name is "." or ".." ||
                    dependency.Name.Split('/').Any(segment => segment is "." or ".."))
                    throw new InvalidOperationException("Angular package.json contains an invalid dependency name.");
                if (!optionalPackages.Contains(dependency.Name)) packages.Add(dependency.Name);
            }
        }
        // An existing directory can be empty or left behind by an interrupted install.
        // Resolve ancestor node_modules too, as npm workspaces can hoist dependencies.
        return packages.Count == 0
            ? !Directory.Exists(Path.Combine(workingDirectory, "node_modules"))
            : packages.Any(package => !IsInstalled(workingDirectory, package));
    }

    private static bool IsInstalled(string workingDirectory, string package)
    {
        for (var folder = new DirectoryInfo(workingDirectory); folder is not null; folder = folder.Parent)
        {
            var packageFolder = Path.Combine(folder.FullName, "node_modules",
                package.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(Path.Combine(packageFolder, "package.json")) &&
                (package != "@angular/cli" || File.Exists(Path.Combine(packageFolder, "bin", "ng.js")))) return true;
        }
        return false;
    }
}
