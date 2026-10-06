using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace FullStackLauncher;

public partial class ServicePortWindow : Window
{
    private readonly Func<string, Task> _save;
    private readonly string _initialPort;
    private readonly bool _editable;
    private bool _saving;
    private bool _saved;
    private bool _closed;

    internal ServicePortWindow(string serviceName, int? port, string? unavailableReason,
        bool stopped, Func<string, Task> save)
    {
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
        PortBox.SelectAll();
    }

    private async void SaveClick(object sender, RoutedEventArgs e)
    {
        if (_saving || !_editable) return;
        try
        {
            var portText = PortBox.Text.Trim();
            if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                || port is < 1 or > 65535)
                throw new InvalidOperationException("Enter a whole-number port from 1 to 65535.");
            if (port.ToString(CultureInfo.InvariantCulture) == _initialPort)
            {
                _saved = true;
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
            DialogResult = true;
        }
        catch (InvalidOperationException ex) { SetStatus(ex.Message, error: true); }
        catch (Exception) { SetStatus("The port could not be saved. Your draft is retained; check settings access and retry.", error: true); }
        finally
        {
            _saving = false;
            if (!_closed)
            {
                SaveButton.IsEnabled = _editable;
                CancelButton.IsEnabled = true;
                PortBox.IsEnabled = _editable;
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
        if (!_saved && PortBox.Text.Trim() != _initialPort && MessageBox.Show(this,
                "Discard unsaved port changes?", "Change port", MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        _closed = true;
    }
}
