using System.IO;
using System.Reflection;

namespace FullStackLauncher.ProjectTasks;

/// <summary>Stages the bundled, generic browser extension for explicit user installation.</summary>
internal static class BrowserCaptureExtensionPackage
{
    private const string ResourcePrefix = "FullStackLauncher.BrowserCaptureExtension.";
    private static readonly string[] Files = ["manifest.json", "background.js", "options.html", "options.js", "README.md"];

    internal static string EnsureExtracted()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FullStackLauncher", "browser-capture-extension");
        Directory.CreateDirectory(folder);
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var name in Files)
        {
            using var resource = assembly.GetManifestResourceStream(ResourcePrefix + name)
                ?? throw new InvalidOperationException("The browser capture extension is missing from this launcher build.");
            using var buffer = new MemoryStream();
            resource.CopyTo(buffer);
            var bytes = buffer.ToArray();
            var target = Path.Combine(folder, name);
            if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes)) continue;

            var temporary = Path.Combine(folder, "." + name + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllBytes(temporary, bytes);
                File.Move(temporary, target, overwrite: true);
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        return folder;
    }
}
