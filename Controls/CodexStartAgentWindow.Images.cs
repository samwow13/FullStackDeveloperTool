using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FullStackLauncher.ProjectTasks;

namespace FullStackLauncher.Controls;

public partial class CodexStartAgentWindow
{
    private readonly ObservableCollection<MessageImage> _messageImages = [];
    private bool _pastingImage;

    public IReadOnlyList<CodexAgentChatImage> DraftImages =>
        _messageImages.Select(image => image.Attachment).ToArray();

    private sealed record MessageImage(CodexAgentChatImage Attachment, BitmapSource Preview)
    {
        public string Id => Attachment.Id;
        public string Caption => Attachment.Caption;
    }

    private void InitializeMessageImages(IReadOnlyList<CodexAgentChatImage>? images)
    {
        MessageImages.ItemsSource = _messageImages;
        if (images is { Count: > 0 }) RestoreMessageImages(images);
    }

    private void RestoreMessageImages(IReadOnlyList<CodexAgentChatImage> images)
    {
        CodexAgentImageStaging.ValidateImages(images);
        // Decode before replacing the draft, so a failed image leaves the existing draft intact.
        var restored = images.Select(image => new MessageImage(image, MakePreview(image))).ToArray();
        _messageImages.Clear();
        foreach (var image in restored) _messageImages.Add(image);
        UpdateMessageImages();
    }

    private void ClearMessageImages()
    {
        _messageImages.Clear();
        UpdateMessageImages();
    }

    private void UpdateMessageImages()
    {
        MessageImages.Visibility = _messageImages.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateControls();
    }

    private bool CanPasteMessageImage => !PromptBox.IsReadOnly && !_pastingImage && !_closed;

    private static bool HasClipboardImage(IDataObject? data)
    {
        try
        {
            return data is not null &&
                (data.GetDataPresent("PNG") || data.GetDataPresent(DataFormats.Bitmap, autoConvert: true));
        }
        catch (Exception) { return false; }
    }

    private void Message_PreviewCanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste || !CanPasteMessageImage) return;
        try
        {
            if (!HasClipboardImage(Clipboard.GetDataObject())) return;
            e.CanExecute = true;
            e.Handled = true;
        }
        catch (Exception) { }
    }

    private async void Message_PreviewExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste || !CanPasteMessageImage) return;
        try
        {
            var data = Clipboard.GetDataObject();
            if (!HasClipboardImage(data)) return;
            e.Handled = true;
            await PasteMessageImageAsync(data!);
        }
        catch (Exception)
        {
            e.Handled = true;
            ShowStatus("The clipboard image could not be read. Copy the image again and retry. Your message is kept.", error: true);
        }
    }

    private async void Message_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!HasClipboardImage(e.SourceDataObject)) return;
        e.CancelCommand();
        if (CanPasteMessageImage) await PasteMessageImageAsync(e.SourceDataObject);
    }

    private async Task PasteMessageImageAsync(IDataObject data)
    {
        if (!CanPasteMessageImage) return;
        if (_messageImages.Count >= CodexAgentChatImage.MaximumCount)
        {
            ShowStatus($"Attach at most {CodexAgentChatImage.MaximumCount} images. Remove an image before pasting another.", error: true);
            return;
        }
        _pastingImage = true;
        UpdateControls();
        try
        {
            // Clipboard access stays on the STA dispatcher. Freeze pixels before background encoding.
            var bitmap = ReadClipboardImage(data);
            var attachment = await Task.Run(() => EncodeMessageImage(bitmap));
            if (_closed) return;
            CodexAgentImageStaging.ValidateImages(DraftImages.Append(attachment).ToArray());
            _messageImages.Add(new MessageImage(attachment, MakePreview(attachment)));
            ClearStatus();
            UpdateMessageImages();
        }
        catch (Exception)
        {
            if (!_closed) ShowStatus("The image could not be attached. Use a smaller PNG or JPEG; each image must fit within 8 MB and all images within 24 MB. Your message and existing images are kept.", error: true);
        }
        finally
        {
            _pastingImage = false;
            if (!_closed)
            {
                UpdateControls();
                PromptBox.Focus();
            }
        }
    }

    private static BitmapSource ReadClipboardImage(IDataObject data)
    {
        BitmapSource? source = null;
        if (data.GetDataPresent("PNG"))
        {
            var png = data.GetData("PNG");
            byte[]? bytes = png switch
            {
                MemoryStream stream => stream.Length <= 40_000_000 ? stream.ToArray() : throw new ArgumentException("Clipboard image is too large."),
                byte[] buffer => buffer.Length <= 40_000_000 ? buffer : throw new ArgumentException("Clipboard image is too large."),
                _ => null
            };
            if (bytes is not null)
            {
                using var input = new MemoryStream(bytes, writable: false);
                var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                var frame = decoder.Frames.FirstOrDefault() ?? throw new ArgumentException("Clipboard image has no frame.");
                if (frame.PixelWidth < 1 || frame.PixelHeight < 1 || (long)frame.PixelWidth * frame.PixelHeight > 100_000_000)
                    throw new ArgumentException("Clipboard image dimensions are unsupported.");
                input.Position = 0;
                var decoded = new BitmapImage();
                decoded.BeginInit();
                decoded.CacheOption = BitmapCacheOption.OnLoad;
                decoded.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                decoded.DecodePixelWidth = Math.Max(1, (int)Math.Round(frame.PixelWidth *
                    Math.Min(1.0, 2560.0 / Math.Max(frame.PixelWidth, frame.PixelHeight))));
                decoded.StreamSource = input;
                decoded.EndInit();
                source = decoded;
            }
        }
        source ??= data.GetData(DataFormats.Bitmap, autoConvert: true) as BitmapSource;
        source ??= Clipboard.GetImage();
        if (source is null || source.PixelWidth < 1 || source.PixelHeight < 1 ||
            (long)source.PixelWidth * source.PixelHeight > 100_000_000)
            throw new ArgumentException("Clipboard image dimensions are unsupported.");
        source = source.CloneCurrentValue();
        source.Freeze();
        return source;
    }

    private static CodexAgentChatImage EncodeMessageImage(BitmapSource source)
    {
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
            throw new ArgumentException("Clipboard image exceeds the attachment limit.");
        return new CodexAgentChatImage
        {
            Caption = "Pasted image", MimeType = mimeType, DataBase64 = Convert.ToBase64String(output.ToArray())
        };
    }

    private static BitmapSource MakePreview(CodexAgentChatImage image)
    {
        using var input = new MemoryStream(Convert.FromBase64String(image.DataBase64), writable: false);
        var preview = new BitmapImage();
        preview.BeginInit();
        preview.CacheOption = BitmapCacheOption.OnLoad;
        preview.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        preview.DecodePixelWidth = 192;
        preview.StreamSource = input;
        preview.EndInit();
        preview.Freeze();
        return preview;
    }

    private void RemoveMessageImage_Click(object sender, RoutedEventArgs e)
    {
        if (!CanPasteMessageImage || sender is not Button { Tag: string id }) return;
        var image = _messageImages.FirstOrDefault(image => image.Id == id);
        if (image is null) return;
        _messageImages.Remove(image);
        UpdateMessageImages();
        PromptBox.Focus();
    }
}
