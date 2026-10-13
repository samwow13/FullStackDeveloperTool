using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FullStackLauncher.ProjectTasks;

internal static partial class CodexAgentImageStaging
{
    /// <summary>Encode frozen clipboard or desktop pixels using the existing attachment budget.</summary>
    internal static CodexAgentChatImage EncodeBitmap(BitmapSource source, string caption)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.PixelWidth < 1 || source.PixelHeight < 1 ||
            (long)source.PixelWidth * source.PixelHeight > 100_000_000)
            throw new ArgumentException("The image dimensions are unsupported. Select a smaller area.");
        var scale = Math.Min(1.0, 2560.0 / Math.Max(source.PixelWidth, source.PixelHeight));
        if (scale < 1)
        {
            source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            source.Freeze();
        }
        using var output = new MemoryStream();
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(source));
        png.Save(output);
        var mimeType = "image/png";
        if (output.Length > CodexAgentChatImage.MaximumBytes)
        {
            output.SetLength(0);
            output.Position = 0;
            var jpeg = new JpegBitmapEncoder { QualityLevel = 90 };
            jpeg.Frames.Add(BitmapFrame.Create(source));
            jpeg.Save(output);
            mimeType = "image/jpeg";
        }
        if (output.Length == 0 || output.Length > CodexAgentChatImage.MaximumBytes)
            throw new ArgumentException("The image exceeds the 8 MB attachment limit. Select a smaller area.");
        return new CodexAgentChatImage
        {
            Caption = caption, MimeType = mimeType, DataBase64 = Convert.ToBase64String(output.ToArray())
        };
    }

    /// <summary>Bound the longest decoded edge without upscaling thin or tiny images.</summary>
    internal static BitmapSource CreatePreview(CodexAgentChatImage image, int maximumEdge = 192)
    {
        if (maximumEdge is < 1 or > 2560) throw new ArgumentOutOfRangeException(nameof(maximumEdge));
        using var input = new MemoryStream(DecodeImage(image), writable: false);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames.FirstOrDefault() ?? throw new ArgumentException("This image has no readable frame.");
        if (frame.PixelWidth < 1 || frame.PixelHeight < 1 || (long)frame.PixelWidth * frame.PixelHeight > 100_000_000)
            throw new ArgumentException("This image's dimensions are unsupported.");
        input.Position = 0;
        var preview = new BitmapImage();
        preview.BeginInit();
        preview.CacheOption = BitmapCacheOption.OnLoad;
        preview.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        var scale = Math.Min(1.0, (double)maximumEdge / Math.Max(frame.PixelWidth, frame.PixelHeight));
        if (frame.PixelWidth >= frame.PixelHeight)
            preview.DecodePixelWidth = Math.Max(1, (int)Math.Round(frame.PixelWidth * scale));
        else
            preview.DecodePixelHeight = Math.Max(1, (int)Math.Round(frame.PixelHeight * scale));
        preview.StreamSource = input;
        preview.EndInit();
        preview.Freeze();
        return preview;
    }
}
