using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace FullStackLauncher;

public partial class LanComputersWindow
{
    private void InitializeLanTheme()
    {
        SourceInitialized += (_, _) => ApplyLanWindowChrome();
    }

    private void ApplyLanWindowChrome()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var enabled = 1;
        // Attribute 20 is supported by current Windows versions; 19 covers older builds.
        if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));

        // Windows 11 supports explicit caption colors; earlier Windows keeps its dark chrome.
        var captionColor = 0x001E1510; // BackgroundBrush: #10151E, in COLORREF order.
        var textColor = 0x00FCF3ED; // TextBrush: #EDF3FC, in COLORREF order.
        DwmSetWindowAttribute(handle, 35, ref captionColor, sizeof(int));
        DwmSetWindowAttribute(handle, 36, ref textColor, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
