using System.IO;
using System.Text.Json;

namespace FullStackLauncher.ProjectTasks;

/// <summary>Bounded clipboard images and private files owned by one saved chat attempt.</summary>
internal static class CodexAgentImageStaging
{
    internal static void ValidateImages(IReadOnlyList<CodexAgentChatImage>? images)
    {
        if (images is null || images.Count > CodexAgentChatImage.MaximumCount)
            throw new ArgumentException("Attach at most 12 images to an agent message.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var totalBytes = 0;
        foreach (var image in images)
        {
            if (image is null || !Guid.TryParseExact(image.Id, "N", out _) || !ids.Add(image.Id))
                throw new ArgumentException("An attached image has an invalid or duplicate identifier.");
            if (image.Caption is null || image.Caption.Length > 240)
                throw new ArgumentException("An attached image caption must be at most 240 characters.");
            if (image.MimeType is not ("image/png" or "image/jpeg"))
                throw new ArgumentException("Attached images must be PNG or JPEG.");
            var bytes = DecodeImage(image);
            if (image.MimeType == "image/png" && !bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                || image.MimeType == "image/jpeg" && !bytes.AsSpan().StartsWith(new byte[] { 255, 216, 255 }))
                throw new ArgumentException("An attached image has an invalid format.");
            totalBytes += bytes.Length;
            if (totalBytes > CodexAgentChatImage.MaximumTotalBytes)
                throw new ArgumentException("Attached images must total no more than 24 MB.");
        }
    }

    internal static IReadOnlyList<string> Stage(CodexAgentChatReceipt receipt)
    {
        ValidateImages(receipt.Request.Images);
        if (receipt.Request.Images.Count == 0) return [];
        var directory = GetDirectory(receipt);
        if (Directory.Exists(directory))
            throw new IOException("Image staging already exists for this agent chat attempt.");
        var paths = new List<string>(receipt.Request.Images.Count);
        try
        {
            Directory.CreateDirectory(directory);
            for (var index = 0; index < receipt.Request.Images.Count; index++)
            {
                var path = GetPath(receipt, index);
                using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    output.Write(DecodeImage(receipt.Request.Images[index]));
                    output.Flush(flushToDisk: true);
                }
                paths.Add(path);
            }
            return paths.AsReadOnly();
        }
        catch
        {
            Cleanup(receipt);
            throw;
        }
    }

    /// <summary>Confirm the exact saved text and ordered images on the matching turn.</summary>
    internal static bool MatchesSubmittedInput(JsonElement turn, CodexAgentChatReceipt receipt, string prompt)
    {
        if (turn.ValueKind != JsonValueKind.Object || !turn.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array) return false;
        var matches = 0;
        foreach (var item in items.EnumerateArray())
        {
            if (OptionalString(item, "type") != "userMessage") continue;
            if (++matches != 1 || !item.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array
                || content.GetArrayLength() != receipt.Request.Images.Count + 1) return false;
            if (OptionalString(content[0], "type") != "text"
                || !string.Equals(OptionalString(content[0], "text"), prompt, StringComparison.Ordinal)) return false;
            for (var index = 0; index < receipt.Request.Images.Count; index++)
            {
                var image = content[index + 1];
                var type = OptionalString(image, "type");
                var value = OptionalString(image, type == "localImage" ? "path" : "url");
                if (!MatchesStoredImage(receipt, index, type, value)) return false;
            }
        }
        return matches == 1;
    }

    internal static bool MatchesStoredImage(CodexAgentChatReceipt receipt, int index, string? type, string? value)
    {
        if (value is null || index < 0 || index >= receipt.Request.Images.Count) return false;
        var image = receipt.Request.Images[index];
        try
        {
            if (type == "image")
            {
                var prefix = "data:" + image.MimeType + ";base64,";
                if (!value.StartsWith(prefix, StringComparison.Ordinal)
                    || value.Length > prefix.Length + ((CodexAgentChatImage.MaximumBytes + 2) / 3) * 4 + 4) return false;
                return Convert.FromBase64String(value[prefix.Length..]).AsSpan().SequenceEqual(DecodeImage(image));
            }
            if (type != "localImage") return false;
            var expected = GetPath(receipt, index);
            if (!Path.IsPathFullyQualified(value)
                || !string.Equals(Path.GetFullPath(value), expected, StringComparison.OrdinalIgnoreCase)) return false;
            using var stream = new FileStream(expected, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bytes = DecodeImage(image);
            if (stream.Length != bytes.Length) return false;
            var actual = new byte[bytes.Length];
            stream.ReadExactly(actual);
            return actual.AsSpan().SequenceEqual(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or FormatException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>Use only after an exact terminal result or before submission is attempted.</summary>
    internal static void Cleanup(CodexAgentChatReceipt receipt)
    {
        if (receipt.Request.Images.Count == 0 || !Guid.TryParseExact(receipt.AttemptId, "N", out _)) return;
        for (var index = 0; index < receipt.Request.Images.Count; index++)
        {
            try { File.Delete(GetPath(receipt, index)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        try { Directory.Delete(GetDirectory(receipt), recursive: false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static byte[] DecodeImage(CodexAgentChatImage image)
    {
        if (image.DataBase64 is null || image.DataBase64.Length == 0
            || image.DataBase64.Length > ((CodexAgentChatImage.MaximumBytes + 2) / 3) * 4 + 4)
            throw new ArgumentException("Each attached image must be no larger than 8 MB.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(image.DataBase64); }
        catch (FormatException ex) { throw new ArgumentException("An attached image contains invalid data.", ex); }
        if (bytes.Length == 0 || bytes.Length > CodexAgentChatImage.MaximumBytes)
            throw new ArgumentException("Each attached image must be no larger than 8 MB.");
        return bytes;
    }

    private static string GetPath(CodexAgentChatReceipt receipt, int index) => Path.Combine(GetDirectory(receipt),
        index.ToString("D2") + (receipt.Request.Images[index].MimeType == "image/png" ? ".png" : ".jpg"));

    private static string GetDirectory(CodexAgentChatReceipt receipt)
    {
        if (!Guid.TryParseExact(receipt.AttemptId, "N", out _))
            throw new ArgumentException("Image staging requires a valid agent chat attempt.");
        return Path.GetFullPath(Path.Combine(CodexAgentStorage.ResolvePath("codex-agent-chats"), "images", receipt.AttemptId));
    }

    private static string? OptionalString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var result)
            && result.ValueKind == JsonValueKind.String ? result.GetString() : null;
}
