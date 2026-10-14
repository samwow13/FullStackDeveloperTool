using System.Text.Json;

namespace FullStackLauncher.ProjectTasks;

/// <summary>A rectangle in physical virtual-desktop pixels. Monitor origins may be negative.</summary>
public sealed record CodexCaptureRectangle(int Left, int Top, int Width, int Height)
{
    internal void Validate()
    {
        if (Width < 1 || Height < 1 || Width > 100_000 || Height > 100_000 ||
            Left is < -1_000_000 or > 1_000_000 || Top is < -1_000_000 or > 1_000_000)
            throw new ArgumentException("Attached capture coordinates exceed the supported limits.");
    }
}

/// <summary>Observed desktop metadata frozen with the approved image, never refreshed at submission.</summary>
public sealed record CodexAgentCaptureContext
{
    private static readonly JsonSerializerOptions ReviewOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public DateTimeOffset CapturedAtUtc { get; init; }
    public DateTimeOffset? MetadataReadAtUtc { get; init; }
    public string Application { get; init; } = "";
    public string WindowTitle { get; init; } = "";
    public string BrowserTabTitle { get; init; } = "";
    public string ObservedAddress { get; init; } = "";
    public string MetadataStatus { get; init; } = "unavailable";
    public string AddressStatus { get; init; } = "unavailable";
    public string WindowAssociation { get; init; } = "window_at_drag_start";
    public string CoordinateSpace => "physical_virtual_desktop_pixels";
    public CodexCaptureRectangle? SourceWindowBounds { get; init; }
    public CodexCaptureRectangle CropBounds { get; init; } = new(0, 0, 1, 1);
    public int OriginalImageWidth { get; init; }
    public int OriginalImageHeight { get; init; }
    public int SentImageWidth { get; init; }
    public int SentImageHeight { get; init; }

    public void Validate()
    {
        if (CapturedAtUtc == default || CapturedAtUtc.Offset != TimeSpan.Zero ||
            MetadataReadAtUtc is { } read && (read == default || read.Offset != TimeSpan.Zero))
            throw new ArgumentException("Attached capture timestamps must use UTC.");
        RequireText(Application, 128);
        RequireText(WindowTitle, 1024);
        RequireText(BrowserTabTitle, 1024);
        RequireText(ObservedAddress, 4096);
        RequireText(MetadataStatus, 128);
        RequireText(AddressStatus, 128);
        RequireText(WindowAssociation, 128);
        if (ObservedAddress.Length > 0 && !BrowserTabAccess.IsSupportedObservedAddress(ObservedAddress))
            throw new ArgumentException("Attached capture address is not a supported browser address.");
        SourceWindowBounds?.Validate();
        if (CropBounds is null) throw new ArgumentException("Attached capture coordinates are missing.");
        CropBounds.Validate();
        if (OriginalImageWidth != CropBounds.Width || OriginalImageHeight != CropBounds.Height ||
            (long)OriginalImageWidth * OriginalImageHeight > 100_000_000 ||
            SentImageWidth < 1 || SentImageHeight < 1 || SentImageWidth > OriginalImageWidth ||
            SentImageHeight > OriginalImageHeight)
            throw new ArgumentException("Attached capture image dimensions do not match the captured region.");
    }

    public string FormatForReview()
    {
        Validate();
        return JsonSerializer.Serialize(this, ReviewOptions).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static void RequireText(string text, int maximum)
    {
        if (text is null || text.Length > maximum || text.Any(char.IsControl))
            throw new ArgumentException("Attached capture metadata exceeds its supported field limits.");
    }
}
