using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FullStackLauncher.ProjectTasks;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class ToolBrowserSetupWindow : Window
{
    private const string ExtensionOptionsUrl = "chrome-extension://poppjfplkbpgbabkabcbhdaifmbfcijf/options.html";
    private readonly DispatcherTimer _statusTimer;
    private string _extensionFolder = "";
    private string _pairingCode = "";
    private bool _ready;
    private bool _openingBrowser;
    private bool _closed;

    private ToolBrowserSetupWindow()
    {
        FullStackLauncher.Services.WindowTheme.Attach(this);
        InitializeComponent();
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        MaxHeight = SystemParameters.WorkArea.Height;
        _statusTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += StatusTimer_Tick;
        Loaded += Window_Loaded;
        Closing += Window_Closing;
        Closed += Window_Closed;
    }

    public static void Show(Window owner) => new ToolBrowserSetupWindow { Owner = owner }.ShowDialog();

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var prepared = await Task.Run(() => (
                Folder: BrowserCaptureExtensionPackage.EnsureExtracted(),
                Code: BrowserPageCaptureBroker.GetPairingCode(),
                Browsers: WebsiteBrowserLauncher.GetInstalledBrowsers()));
            if (_closed) return;
            _extensionFolder = prepared.Folder;
            _pairingCode = prepared.Code;
            FolderText.Text = _extensionFolder;
            if (DetailsExpander.IsExpanded) PairingCodeText.Text = _pairingCode;
            BrowserPicker.ItemsSource = prepared.Browsers;
            var status = BrowserPageCaptureBroker.GetBrowserSetupStatus();
            BrowserPicker.SelectedItem = prepared.Browsers.FirstOrDefault(browser => browser.Id == status.BrowserId)
                ?? prepared.Browsers.FirstOrDefault();
            _ready = true;
            UpdateButtons();
            UpdateConnectionStatus();
            _statusTimer.Start();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or
            InvalidOperationException or ArgumentException or SecurityException)
        {
            if (_closed) return;
            ConnectionStatusText.Text = "Browser setup could not prepare its extension or pairing code. Close this window and retry.";
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_openingBrowser) return;
        e.Cancel = true;
        Report("Wait for the browser opening request to finish, then close setup.");
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _closed = true;
        _statusTimer.Stop();
        _statusTimer.Tick -= StatusTimer_Tick;
        PairingCodeText.Clear();
        _pairingCode = "";
    }

    private void StatusTimer_Tick(object? sender, EventArgs e) => UpdateConnectionStatus();

    private void UpdateConnectionStatus()
    {
        if (!_ready || _closed) return;
        var status = BrowserPageCaptureBroker.GetBrowserSetupStatus();
        if (ConnectionStatusText.Text != status.Status) ConnectionStatusText.Text = status.Status;
    }

    private void BrowserPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Initial XAML events can run before the named controls have been connected.
        if (CloseButton == null) return;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var hasBrowser = BrowserPicker.SelectedItem is InstalledWebsiteBrowser;
        BrowserPicker.IsEnabled = !_openingBrowser;
        BrowserHelpText.Text = hasBrowser
            ? "Use the same browser profile for both steps."
            : "Chrome or Edge was not found. Install one, then reopen setup. You can still copy the folder and pairing code.";
        OpenExtensionsButton.IsEnabled = _ready && hasBrowser && !_openingBrowser;
        OpenOptionsButton.IsEnabled = _ready && hasBrowser && !_openingBrowser;
        CopyFolderButton.IsEnabled = _ready;
        OpenFolderButton.IsEnabled = _ready && !_openingBrowser;
        CopyCodeButton.IsEnabled = _ready;
        CloseButton.IsEnabled = !_openingBrowser;
        UpdateConnectionStatus();
    }

    private async void OpenExtensions_Click(object sender, RoutedEventArgs e) => await OpenBrowserSetupAsync(false);
    private async void OpenOptions_Click(object sender, RoutedEventArgs e) => await OpenBrowserSetupAsync(true);

    private async Task OpenBrowserSetupAsync(bool options)
    {
        if (_openingBrowser || BrowserPicker.SelectedItem is not InstalledWebsiteBrowser selected) return;
        _openingBrowser = true;
        UpdateButtons();
        try
        {
            await Task.Run(() =>
            {
                // Resolve only a supported installed executable. These fixed targets never
                // contain pairing material, credentials, an arbitrary URL or shell syntax.
                var browser = WebsiteBrowserLauncher.Resolve(selected.Id);
                var target = options ? ExtensionOptionsUrl
                    : selected.Id == "chrome" ? "chrome://extensions/" : "edge://extensions/";
                var start = new ProcessStartInfo(browser.FileName) { UseShellExecute = false };
                start.ArgumentList.Add(target);
                using var process = Process.Start(start)
                    ?? throw new InvalidOperationException("Browser did not start.");
            });
            Report(options
                ? "Pairing page requested. Paste the copied code and save. If it does not open, load or reload the extension first."
                : "Extensions page requested. Enable Developer mode, then load the copied folder or reload the existing extension.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or
            ArgumentException or Win32Exception or SecurityException)
        {
            Report(options
                ? "Browser pairing page could not open. In Extensions, find FullStack Launcher Page Context and open its Options."
                : "Browser Extensions page could not open. Open Extensions from the selected browser's menu.");
        }
        finally
        {
            _openingBrowser = false;
            UpdateButtons();
        }
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready || _openingBrowser) return;
        _openingBrowser = true;
        UpdateButtons();
        try
        {
            await Task.Run(() =>
            {
                using var process = Process.Start(new ProcessStartInfo(_extensionFolder) { UseShellExecute = true });
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or
            ArgumentException or Win32Exception or SecurityException)
        { Report("The extension folder could not open. Copy its path instead."); }
        finally
        {
            _openingBrowser = false;
            UpdateButtons();
        }
    }

    private void CopyFolder_Click(object sender, RoutedEventArgs e) =>
        CopyValue(_extensionFolder, "Extension folder copied. Paste it into the Load unpacked folder picker.",
            "Clipboard is unavailable. Expand Advanced, then select and copy the extension folder.");

    private void CopyCode_Click(object sender, RoutedEventArgs e) =>
        CopyValue(_pairingCode, "Pairing code copied. Paste it on the browser pairing page and save.",
            "Clipboard is unavailable. Expand Advanced, then select and copy the pairing code.");

    private void CopyValue(string value, string success, string failure)
    {
        if (!_ready || string.IsNullOrEmpty(value)) return;
        try { Clipboard.SetText(value); Report(success); }
        catch (Exception) { Report(failure); }
    }

    private void Details_Expanded(object sender, RoutedEventArgs e)
    {
        if (PairingCodeText != null) PairingCodeText.Text = _pairingCode;
    }

    private void Details_Collapsed(object sender, RoutedEventArgs e)
    {
        if (PairingCodeText != null) PairingCodeText.Clear();
    }

    private void Report(string text)
    {
        if (_closed) return;
        StatusText.Text = text;
        StatusText.Visibility = Visibility.Visible;
    }
}
