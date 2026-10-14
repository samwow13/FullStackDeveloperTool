using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace FullStackLauncher.Controls;

public partial class DesktopContextGathererWindow
{
    private static readonly Brush JsonPropertyBrush = JsonBrush("#9CDCFE");
    private static readonly Brush JsonStringBrush = JsonBrush("#CE9178");
    private static readonly Brush JsonNumberBrush = JsonBrush("#B5CEA8");
    private static readonly Brush JsonBooleanBrush = JsonBrush("#569CD6");
    private static readonly Brush JsonNullBrush = JsonBrush("#C586C0");
    private string? _displayedMetadataJson;

    private void ShowMetadataJson(string json)
    {
        // Status updates must not reset the current selection or scroll position.
        if (string.Equals(_displayedMetadataJson, json, StringComparison.Ordinal)) return;

        var paragraph = new Paragraph { Margin = new Thickness(0) };
        var utf8 = Encoding.UTF8.GetBytes(json);
        var reader = new Utf8JsonReader(utf8);
        var offset = 0;
        try
        {
            while (reader.Read())
            {
                var start = checked((int)reader.TokenStartIndex);
                // Property-name consumption includes its colon. Keep that colon
                // and all gaps neutral, preserving the original serialized text.
                var length = reader.TokenType is JsonTokenType.PropertyName or JsonTokenType.String
                    ? reader.ValueSpan.Length + 2
                    : checked((int)reader.BytesConsumed) - start;
                AddJsonRun(paragraph, utf8, offset, start - offset, MetadataJson.Foreground);
                var brush = reader.TokenType switch
                {
                    JsonTokenType.PropertyName => JsonPropertyBrush,
                    JsonTokenType.String => JsonStringBrush,
                    JsonTokenType.Number => JsonNumberBrush,
                    JsonTokenType.True or JsonTokenType.False => JsonBooleanBrush,
                    JsonTokenType.Null => JsonNullBrush,
                    _ => MetadataJson.Foreground
                };
                AddJsonRun(paragraph, utf8, start, length, brush);
                offset = start + length;
            }
            AddJsonRun(paragraph, utf8, offset, utf8.Length - offset, MetadataJson.Foreground);
        }
        catch (JsonException)
        {
            // A display-only colorizer must never hide the original content.
            paragraph.Inlines.Clear();
            paragraph.Inlines.Add(new Run(json));
        }

        var typeface = new Typeface(MetadataJson.FontFamily, MetadataJson.FontStyle,
            MetadataJson.FontWeight, MetadataJson.FontStretch);
        var text = new FormattedText(json, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, MetadataJson.FontSize, MetadataJson.Foreground,
            VisualTreeHelper.GetDpi(MetadataJson).PixelsPerDip);
        MetadataJson.Document = new FlowDocument(paragraph)
        {
            PagePadding = new Thickness(0),
            FontFamily = MetadataJson.FontFamily,
            FontSize = MetadataJson.FontSize,
            Foreground = MetadataJson.Foreground,
            LineHeight = 16,
            // Fit the longest original line; horizontal scrolling replaces wrapping.
            PageWidth = Math.Max(1, text.WidthIncludingTrailingWhitespace + 2)
        };
        _displayedMetadataJson = json;
        MetadataJson.ScrollToHome();
    }

    private static void AddJsonRun(Paragraph paragraph, byte[] utf8, int start, int length, Brush brush)
    {
        if (length > 0)
            paragraph.Inlines.Add(new Run(Encoding.UTF8.GetString(utf8, start, length)) { Foreground = brush });
    }

    private static Brush JsonBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
