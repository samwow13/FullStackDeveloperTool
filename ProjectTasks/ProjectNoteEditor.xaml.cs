using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FullStackLauncher.ProjectTasks;

public partial class ProjectNoteEditor : UserControl
{
    public ProjectNoteEditor() => InitializeComponent();

    public void FocusPrompt() => NotePrompt.Focus();

    private void NoteEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control) return;
        try
        {
            if (!Clipboard.ContainsImage()) return;
            e.Handled = true;
            PasteClipboardImage();
        }
        catch (Exception)
        {
            if (DataContext is ProjectTasksViewModel model) model.ReportImageError("The clipboard image is unavailable. Copy it again and retry.");
        }
    }

    private async void SnipImage_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ProjectTasksViewModel model) return;
        var editorWindow = Window.GetWindow(this);
        var projectId = model.ProjectId;
        var noteId = model.SelectedNote?.Id;
        try
        {
            var capture = await WindowSnipFlow.CaptureAsync(editorWindow);
            if (capture == null || !editorWindow.IsVisible) return;
            if (!ReferenceEquals(DataContext, model) || model.ProjectId != projectId || model.SelectedNote?.Id != noteId)
            {
                model.ReportImageError("The selected note changed during the snip. Choose the note and snip again.");
                return;
            }
            var (bytes, mimeType) = EncodeImage(capture.Image);
            model.AddImage(bytes, mimeType, "Screen snip", capture.PageContext);
        }
        catch (ArgumentException)
        {
            model.ReportImageError("The snip is too large. Select a smaller area and retry.");
        }
        catch (InvalidOperationException error)
        {
            model.ReportImageError(error.Message);
        }
        catch (Exception)
        {
            model.ReportImageError("The screen snip could not be added. Check the window and try again.");
        }
    }

    private void BrowserSourceSetup_Click(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        try
        {
            var folder = BrowserCaptureExtensionPackage.EnsureExtracted();
            var pairingCode = BrowserPageCaptureBroker.GetPairingCode();
            var dialog = new Window
            {
                Title = "Browser source setup — Full Stack Launcher",
                Owner = owner,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = 690,
                Height = 380,
                MinWidth = 540,
                MinHeight = 340,
                Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"),
                Background = (System.Windows.Media.Brush)FindResource("SurfaceBrush")
            };
            var content = new StackPanel { Margin = new Thickness(20) };
            content.Children.Add(new TextBlock
            {
                Text = "Capture live HTML and CSS with browser snips",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 12)
            });
            content.Children.Add(new TextBlock
            {
                Text = "1. Open Chrome or Edge Extensions and turn on Developer mode.\n" +
                       "2. Select Load unpacked and choose the extension folder below.\n" +
                       "3. Open the extension's Options, paste the pairing code, and save.\n" +
                       "4. Keep the extension enabled when you snip a browser page.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 14)
            });
            content.Children.Add(new TextBlock { Text = "Extension folder", FontWeight = FontWeights.SemiBold });
            content.Children.Add(new TextBox
            {
                Text = folder,
                IsReadOnly = true,
                Margin = new Thickness(0, 4, 0, 8),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            });
            var openFolder = new Button { Content = "Open extension folder", Padding = new Thickness(10, 6, 10, 6),
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
            openFolder.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); }
                catch (Exception) { MessageBox.Show(dialog, "The extension folder could not be opened. Copy the path shown above.",
                    "Browser source setup", MessageBoxButton.OK, MessageBoxImage.Warning); }
            };
            content.Children.Add(openFolder);
            content.Children.Add(new TextBlock { Text = "Pairing code", FontWeight = FontWeights.SemiBold });
            var code = new TextBox { Text = pairingCode, IsReadOnly = true,
                Margin = new Thickness(0, 4, 0, 8) };
            content.Children.Add(code);
            var copyCode = new Button { Content = "Copy pairing code", Padding = new Thickness(10, 6, 10, 6),
                HorizontalAlignment = HorizontalAlignment.Left };
            copyCode.Click += (_, _) =>
            {
                try { Clipboard.SetText(pairingCode); copyCode.Content = "Copied"; }
                catch (Exception) { MessageBox.Show(dialog, "Clipboard is unavailable. Select and copy the code shown above.",
                    "Browser source setup", MessageBoxButton.OK, MessageBoxImage.Warning); }
            };
            content.Children.Add(copyCode);
            dialog.Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            dialog.ShowDialog();
        }
        catch (Exception)
        {
            if (DataContext is ProjectTasksViewModel model)
                model.ReportImageError("Browser source setup is unavailable. Check the extension files and local storage, then retry.");
        }
    }

    private void SaveAndClear_Click(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (DataContext is ProjectTasksViewModel { SelectedNote: null, HasCurrentDraft: false })
                FocusPrompt();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void PasteClipboardImage()
    {
        if (DataContext is not ProjectTasksViewModel model) return;
        try
        {
            var bitmap = Clipboard.ContainsImage() ? Clipboard.GetImage() : null;
            if (bitmap == null)
            {
                model.ReportImageError("No image is available on the clipboard.");
                return;
            }
            var (bytes, mimeType) = EncodeImage(bitmap);
            model.AddImage(bytes, mimeType);
        }
        catch (Exception)
        {
            model.ReportImageError("The clipboard image could not be added. Use a smaller PNG or JPEG image and retry.");
        }
    }

    private void AddImageFile_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ProjectTasksViewModel model) return;
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add an existing application screenshot or image",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files|*.*"
        };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            if (new FileInfo(picker.FileName).Length > 40_000_000)
                throw new ArgumentException("Image file is too large.");
            int pixelWidth;
            int pixelHeight;
            using (var input = File.OpenRead(picker.FileName))
            {
                var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                var frame = decoder.Frames.FirstOrDefault() ?? throw new ArgumentException("Image has no frame.");
                pixelWidth = frame.PixelWidth;
                pixelHeight = frame.PixelHeight;
            }
            if (pixelWidth < 1 || pixelHeight < 1 || (long)pixelWidth * pixelHeight > 100_000_000)
                throw new ArgumentException("Image dimensions are unsupported.");
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            var scale = Math.Min(1.0, 2560.0 / Math.Max(pixelWidth, pixelHeight));
            bitmap.DecodePixelWidth = Math.Max(1, (int)Math.Round(pixelWidth * scale));
            bitmap.UriSource = new Uri(picker.FileName, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            var (bytes, mimeType) = EncodeImage(bitmap);
            model.AddImage(bytes, mimeType, Path.GetFileNameWithoutExtension(picker.FileName));
        }
        catch (Exception)
        {
            model.ReportImageError("The image file could not be added. Choose a readable image smaller than 40 MB.");
        }
    }

    private static (byte[] Bytes, string MimeType) EncodeImage(BitmapSource source)
    {
        if (source.PixelWidth < 1 || source.PixelHeight < 1 || (long)source.PixelWidth * source.PixelHeight > 100_000_000)
            throw new ArgumentException("Image dimensions are unsupported.");
        var scale = Math.Min(1.0, 2560.0 / Math.Max(source.PixelWidth, source.PixelHeight));
        if (scale < 1)
        {
            var reduced = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            reduced.Freeze();
            source = reduced;
        }
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(source));
        using var output = new MemoryStream();
        png.Save(output);
        if (output.Length <= ProjectTaskNoteImage.MaximumBytes) return (output.ToArray(), "image/png");
        output.SetLength(0);
        var jpeg = new JpegBitmapEncoder { QualityLevel = 90 };
        jpeg.Frames.Add(BitmapFrame.Create(source));
        jpeg.Save(output);
        if (output.Length > ProjectTaskNoteImage.MaximumBytes)
            throw new ArgumentException("Image exceeds the note attachment limit.");
        return (output.ToArray(), "image/jpeg");
    }

    private void RemoveImage_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ProjectTasksViewModel model && sender is FrameworkElement { DataContext: NoteImageRow image })
            model.RemoveImage(image.Id);
    }

    private void OpenPageSource_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: NoteImageRow image } || !image.HasPageSource) return;
        var dialog = new Window
        {
            Owner = Window.GetWindow(this),
            Title = "Captured browser page source",
            Width = 1100, Height = 750, MinWidth = 550, MinHeight = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Foreground = (Brush)FindResource("TextBrush"),
            Background = (Brush)FindResource("SurfaceBrush")
        };
        var layout = new Grid { Margin = new Thickness(16) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var details = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        details.Children.Add(new TextBlock { Text = image.PageUrl, TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold });
        details.Children.Add(new TextBlock { Text = image.PageSourceSummary, TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("MutedBrush"), Margin = new Thickness(0, 6, 0, 0) });
        layout.Children.Add(details);

        static TextBox SourceText(string value) => new()
        {
            Text = value, IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 12,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.NoWrap
        };
        var sources = new Grid();
        sources.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sources.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        sources.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sources.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        sources.Children.Add(new TextBlock { Text = "HTML", FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 5) });
        var html = SourceText(image.PageHtml);
        Grid.SetRow(html, 1);
        sources.Children.Add(html);
        var cssLabel = new TextBlock { Text = "CSS", FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 5) };
        Grid.SetRow(cssLabel, 2);
        sources.Children.Add(cssLabel);
        var css = SourceText(image.PageCss);
        Grid.SetRow(css, 3);
        sources.Children.Add(css);
        Grid.SetRow(sources, 1);
        layout.Children.Add(sources);
        dialog.Content = layout;
        dialog.ShowDialog();
    }

    private void OpenImage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: NoteImageRow image }) return;
        BitmapSource preview;
        try { preview = image.GetFullImage(); }
        catch (Exception)
        {
            if (DataContext is ProjectTasksViewModel model) model.ReportImageError("This saved image could not be opened.");
            return;
        }
        var viewer = new Window
        {
            Owner = Window.GetWindow(this),
            Title = image.Caption,
            Width = 1100, Height = 800, MinWidth = 500, MinHeight = 350,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new Image { Source = preview, Stretch = Stretch.None, Margin = new Thickness(16) }
            }
        };
        viewer.ShowDialog();
    }
}
