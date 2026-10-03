using System.IO;

namespace FullStackLauncher.ProjectTasks;

/// <summary>Private, per-attempt files for bounded localImage App Server input.</summary>
internal static class QueueImageStaging
{
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FullStackLauncher", "queue-image-staging");

    public static IReadOnlyList<string> Stage(ProjectTaskDispatchSnapshot snapshot)
    {
        if (snapshot.Images.Count == 0) return [];
        var directory = GetDirectory(snapshot);
        if (Directory.Exists(directory))
            throw new IOException("Queue image staging already exists for this attempt.");

        var paths = new List<string>(snapshot.Images.Count);
        try
        {
            Directory.CreateDirectory(directory);
            for (var index = 0; index < snapshot.Images.Count; index++)
            {
                var image = snapshot.Images[index];
                var bytes = Convert.FromBase64String(image.DataBase64);
                if (bytes.Length == 0 || bytes.Length > ProjectTaskNoteImage.MaximumBytes)
                    throw new InvalidDataException("A frozen queue image is empty or too large.");
                var path = GetPath(snapshot, index);
                using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    output.Write(bytes);
                    output.Flush(flushToDisk: true);
                }
                paths.Add(path);
            }
            return paths;
        }
        catch
        {
            Cleanup(snapshot);
            throw;
        }
    }

    public static bool MatchesStoredImage(ProjectTaskDispatchSnapshot snapshot, int index,
        string? type, string? value)
    {
        if (value is null || index < 0 || index >= snapshot.Images.Count) return false;
        var image = snapshot.Images[index];
        if (type == "image")
            return string.Equals(value, "data:" + image.MimeType + ";base64," + image.DataBase64,
                StringComparison.Ordinal);
        if (type != "localImage") return false;
        try
        {
            var expected = GetPath(snapshot, index);
            if (!string.Equals(Path.GetFullPath(value), expected, StringComparison.OrdinalIgnoreCase))
                return false;
            var info = new FileInfo(expected);
            if (!info.Exists || info.Length == 0 || info.Length > ProjectTaskNoteImage.MaximumBytes)
                return false;
            return File.ReadAllBytes(expected).AsSpan().SequenceEqual(Convert.FromBase64String(image.DataBase64));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or FormatException or System.Security.SecurityException)
        {
            return false;
        }
    }

    public static void Cleanup(ProjectTaskDispatchSnapshot snapshot)
    {
        if (snapshot.Images.Count == 0 || !Guid.TryParseExact(snapshot.ImageStagingId, "N", out _)) return;
        for (var index = 0; index < snapshot.Images.Count; index++)
        {
            try { File.Delete(GetPath(snapshot, index)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        try { Directory.Delete(GetDirectory(snapshot), recursive: false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string GetPath(ProjectTaskDispatchSnapshot snapshot, int index)
    {
        var extension = snapshot.Images[index].MimeType == "image/png" ? ".png" : ".jpg";
        return Path.Combine(GetDirectory(snapshot), index.ToString("D2") + extension);
    }

    private static string GetDirectory(ProjectTaskDispatchSnapshot snapshot)
    {
        if (!Guid.TryParseExact(snapshot.ImageStagingId, "N", out _))
            throw new ArgumentException("A frozen image dispatch requires a valid staging ID.");
        return Path.Combine(Root, snapshot.ImageStagingId);
    }
}
