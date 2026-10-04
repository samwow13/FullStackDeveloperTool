using System.Windows;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace FullStackLauncher;

public enum ExitServicesChoice
{
    Cancel,
    LeaveRunning,
    ForceStop
}

public partial class ExitServicesWindow : Window
{
    public ExitServicesWindow(IReadOnlyList<string> runningServices)
    {
        ArgumentNullException.ThrowIfNull(runningServices);
        InitializeComponent();
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);

        var services = runningServices.ToArray();
        RunningServices.ItemsSource = services;
        RunningCount.Text = services.Length == 1
            ? "1 active app"
            : $"{services.Length} active apps";
        Loaded += (_, _) => CancelButton.Focus();
        SourceInitialized += (_, _) =>
        {
            var enabled = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref enabled, sizeof(int));
        };
    }

    public ExitServicesChoice Choice { get; private set; } = ExitServicesChoice.Cancel;

    private void LeaveRunning_Click(object sender, RoutedEventArgs e)
    {
        Choice = ExitServicesChoice.LeaveRunning;
        DialogResult = true;
    }

    private void ForceStop_Click(object sender, RoutedEventArgs e)
    {
        Choice = ExitServicesChoice.ForceStop;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Choice = ExitServicesChoice.Cancel;
        DialogResult = false;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
