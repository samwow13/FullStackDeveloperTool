using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class ServicePortWindow : Window, IDashboardReplacementWindow
{
    public bool IsDashboardReplacementReady => !_saving || _saved;
    public bool IsDashboardReplacementClosePending => false;

    private readonly Func<string, Task> _save;
    private readonly string _initialPort;
    private readonly bool _editable;
    private bool _saving;
    private bool _saved;
    private bool _closed;
    private Action? _clearDashboardResume;
    private bool _dashboardResumePending;
    private bool _dashboardResumeClose;
    private int? _restoredCaretIndex;

    internal void ConfigureDashboardResume(ServicePortEditorResume? state, Action clearDashboardResume)
    {
        _clearDashboardResume = clearDashboardResume;
        if (state is null) return;
        PortBox.Text = state.Port;
        PortBox.CaretIndex = Math.Clamp(state.CaretIndex, 0, PortBox.Text.Length);
        _restoredCaretIndex = PortBox.CaretIndex;
        _dashboardResumePending = true;
        SetStatus("Port draft restored. " + StatusText.Text, error: !_editable);
    }

    internal ServicePortEditorResume CaptureDashboardResume() => new(PortBox.Text, _initialPort, PortBox.CaretIndex);

    internal void MarkDashboardResumeCheckpointed()
    {
        _dashboardResumePending = true;
        _dashboardResumeClose = true;
    }

    private void ClearDashboardResume()
    {
        if (!_dashboardResumePending) return;
        _clearDashboardResume?.Invoke();
        _dashboardResumePending = false;
    }

    internal ServicePortWindow(string serviceName, int? port, string? unavailableReason,
        bool stopped, Func<string, Task> save)
    {
        FullStackLauncher.Services.WindowTheme.Attach(this);
        _save = save;
        _initialPort = port?.ToString(CultureInfo.InvariantCulture) ?? "";
        _editable = unavailableReason is null;
        InitializeComponent();
        Title = $"{serviceName} - Change port";
        ServiceHeading.Text = serviceName;
        PortBox.Text = _initialPort;
        PortBox.IsEnabled = _editable;
        SaveButton.IsEnabled = _editable;
        SetStatus(unavailableReason ?? (stopped
            ? "Applies on the next start."
            : $"Stop {serviceName} before saving a new port."), error: unavailableReason is not null);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_editable) { CancelButton.Focus(); return; }
        PortBox.Focus();
        if (_restoredCaretIndex is { } caret) PortBox.CaretIndex = caret;
        else PortBox.SelectAll();
        _restoredCaretIndex = null;
    }

    private async void SaveClick(object sender, RoutedEventArgs e)
    {
        if (_saving || !_editable) return;
        if (_saved) { Close(); return; }
        try
        {
            var portText = PortBox.Text.Trim();
            if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                || port is < 1 or > 65535)
                throw new InvalidOperationException("Enter a whole-number port from 1 to 65535.");
            if (port.ToString(CultureInfo.InvariantCulture) == _initialPort)
            {
                _saved = true;
                ClearDashboardResume();
                DialogResult = true;
                return;
            }
            _saving = true;
            SaveButton.IsEnabled = false;
            CancelButton.IsEnabled = false;
            PortBox.IsEnabled = false;
            SetStatus("Saving port…");
            await _save(portText);
            _saved = true;
            ClearDashboardResume();
            DialogResult = true;
        }
        catch (InvalidOperationException ex) { SetStatus(ex.Message, error: true); }
        catch (Exception) { SetStatus(_saved
            ? "Port saved, but its old draft checkpoint could not be cleared. Retry closing to finish recovery cleanup."
            : "The port could not be saved. Your draft is retained; check settings access and retry.", error: true); }
        finally
        {
            _saving = false;
            if (!_closed)
            {
                SaveButton.IsEnabled = _editable && !_saved;
                CancelButton.IsEnabled = true;
                PortBox.IsEnabled = _editable && !_saved;
                PortBox.Focus();
            }
        }
    }

    private void SetStatus(string message, bool error = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = error ? new SolidColorBrush(Color.FromRgb(255, 172, 169))
            : (Brush)FindResource("MutedBrush");
    }

    private void CancelClick(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _saving) return;
        e.Handled = true;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_saving && !_saved) { e.Cancel = true; return; }
        if (!_dashboardResumeClose && !_saved && PortBox.Text.Trim() != _initialPort && MessageBox.Show(this,
                "Discard unsaved port changes?", "Change port", MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        if (!_dashboardResumeClose)
        {
            try { ClearDashboardResume(); }
            catch (Exception)
            {
                e.Cancel = true;
                SetStatus("The old port draft checkpoint could not be cleared. The editor remains open; retry closing after checking local storage access.", error: true);
                return;
            }
        }
        _closed = true;
    }
}
