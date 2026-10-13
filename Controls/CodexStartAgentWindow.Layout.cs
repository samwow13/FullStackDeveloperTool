using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace FullStackLauncher.Controls;

public partial class CodexStartAgentWindow
{
    private bool _updatingContentBounds;

    private void InitializeContentSizing()
    {
        SourceInitialized += (_, _) => UpdateContentBounds(useOwner: true);
        Loaded += (_, _) => UpdateContentBounds();
        LocationChanged += (_, _) => { if (IsLoaded) UpdateContentBounds(constrainPosition: false); };
        SizeChanged += (_, _) => { if (IsLoaded) UpdateContentBounds(); };
        DpiChanged += (_, _) => UpdateContentBounds();
    }

    private void ComposerContent_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Resizing remains available, but new content resumes automatic height.
        // This also covers screenshot wrapping when the user changes the width.
        if (!IsLoaded || WindowState != WindowState.Normal) return;
        SizeToContent = SizeToContent.Height;
        UpdateContentBounds();
    }

    private void UpdateContentBounds(bool useOwner = false, bool constrainPosition = true)
    {
        if (_updatingContentBounds || WindowState != WindowState.Normal) return;
        var anchor = useOwner && Owner is not null ? Owner : this;
        var handle = new WindowInteropHelper(anchor).Handle;
        if (handle == IntPtr.Zero) return;

        _updatingContentBounds = true;
        try
        {
            var area = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
            var scale = PresentationSource.FromVisual(anchor)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var workArea = new Rect(area.X * scale.M11, area.Y * scale.M22,
                area.Width * scale.M11, area.Height * scale.M22);
            const double margin = 16;
            var availableHeight = Math.Max(1, workArea.Height - margin * 2);
            MinHeight = Math.Min(440, availableHeight);
            MaxHeight = availableHeight;

            // Growing near the bottom must not put Close and Start Agent off-screen.
            if (constrainPosition && IsLoaded && double.IsFinite(Top))
            {
                var minimumTop = workArea.Top + margin;
                var maximumTop = Math.Max(minimumTop, workArea.Bottom - margin - ActualHeight);
                Top = Math.Clamp(Top, minimumTop, maximumTop);
            }
        }
        finally { _updatingContentBounds = false; }
    }
}
