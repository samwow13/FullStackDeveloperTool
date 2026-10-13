using System.Windows;

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
        FullStackLauncher.Services.WindowTheme.Attach(this);
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
}
