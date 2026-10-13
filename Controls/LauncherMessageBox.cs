using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FullStackLauncher.Services;

namespace FullStackLauncher.Controls;

/// <summary>Uses the shared launcher theme without changing confirmation results.</summary>
internal static class LauncherMessageBox
{
    public static MessageBoxResult Show(string messageBoxText, string caption) =>
        ShowCore(null, messageBoxText, caption, MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);

    public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button,
        MessageBoxImage icon) => ShowCore(null, messageBoxText, caption, button, icon, MessageBoxResult.None);

    public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button,
        MessageBoxImage icon, MessageBoxResult defaultResult) =>
        ShowCore(null, messageBoxText, caption, button, icon, defaultResult);

    public static MessageBoxResult Show(Window? owner, string messageBoxText, string caption, MessageBoxButton button,
        MessageBoxImage icon) => ShowCore(owner, messageBoxText, caption, button, icon, MessageBoxResult.None);

    public static MessageBoxResult Show(Window? owner, string messageBoxText, string caption, MessageBoxButton button,
        MessageBoxImage icon, MessageBoxResult defaultResult) =>
        ShowCore(owner, messageBoxText, caption, button, icon, defaultResult);

    private static MessageBoxResult ShowCore(Window? owner, string message, string caption,
        MessageBoxButton buttons, MessageBoxImage icon, MessageBoxResult defaultResult)
    {
        var app = Application.Current;
        // Before WPF starts, or after its dispatcher has stopped, retain the native error-reporting path.
        if (app is null || app.Dispatcher.HasShutdownStarted || app.Dispatcher.HasShutdownFinished)
            return owner is null
                ? System.Windows.MessageBox.Show(message, caption, buttons, icon, defaultResult)
                : System.Windows.MessageBox.Show(owner, message, caption, buttons, icon, defaultResult);

        if (!app.Dispatcher.CheckAccess())
            return app.Dispatcher.Invoke(() => ShowCore(owner, message, caption, buttons, icon, defaultResult));

        ValidateArguments(buttons, icon, defaultResult);
        owner ??= app.Windows.Cast<Window>().LastOrDefault(window => window.IsVisible && window.IsActive);
        // A not-yet-shown window has no owner HWND. Do not create one merely to display an error.
        if (owner is not null && (!owner.IsVisible || new WindowInteropHelper(owner).Handle == IntPtr.Zero))
            owner = null;

        var previousMainWindow = app.MainWindow;
        var previousShutdownMode = app.ShutdownMode;
        var preserveStartup = previousMainWindow is null;
        if (preserveStartup) app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        MessageWindow? dialog = null;
        try
        {
            dialog = new MessageWindow(message, caption, buttons, icon, defaultResult, owner);
            // The first WPF window otherwise becomes MainWindow and can end startup when dismissed.
            if (preserveStartup && ReferenceEquals(app.MainWindow, dialog)) app.MainWindow = null!;
            dialog.ShowDialog();
            return dialog.Result;
        }
        finally
        {
            if (preserveStartup)
            {
                // A startup continuation can create the real dashboard inside the modal dispatcher loop.
                if (app.MainWindow is null or MessageWindow) app.MainWindow = previousMainWindow;
                if (app.ShutdownMode == ShutdownMode.OnExplicitShutdown) app.ShutdownMode = previousShutdownMode;
            }
        }
    }

    private static void ValidateArguments(MessageBoxButton buttons, MessageBoxImage icon, MessageBoxResult defaultResult)
    {
        if (buttons is not (MessageBoxButton.OK or MessageBoxButton.OKCancel or MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel))
            throw new InvalidEnumArgumentException("button", (int)buttons, typeof(MessageBoxButton));
        if (icon is not (MessageBoxImage.None or MessageBoxImage.Error or MessageBoxImage.Question or MessageBoxImage.Warning or MessageBoxImage.Information))
            throw new InvalidEnumArgumentException("icon", (int)icon, typeof(MessageBoxImage));
        if (defaultResult is not (MessageBoxResult.None or MessageBoxResult.OK or MessageBoxResult.Cancel or MessageBoxResult.Yes or MessageBoxResult.No))
            throw new InvalidEnumArgumentException("defaultResult", (int)defaultResult, typeof(MessageBoxResult));
    }

    private sealed class MessageWindow : Window, IDashboardReplacementWindow
    {
        private readonly MessageBoxButton _buttons;
        private readonly Button _defaultButton;
        public MessageBoxResult Result { get; private set; }

        // Replacement must wait for the user's answer, as it does for a native message box.
        public bool IsDashboardReplacementReady => false;
        public bool IsDashboardReplacementClosePending => false;

        internal MessageWindow(string message, string caption, MessageBoxButton buttons, MessageBoxImage icon,
            MessageBoxResult defaultResult, Window? owner)
        {
            FullStackLauncher.Services.WindowTheme.Attach(this);
            _buttons = buttons;
            Title = caption;
            Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/launcher.ico", UriKind.Absolute));
            Owner = owner;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
            SizeToContent = SizeToContent.Height;
            SetResourceReference(BackgroundProperty, "BackgroundBrush");
            SetResourceReference(ForegroundProperty, "TextBrush");
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 13;

            var workArea = GetWorkArea(owner);
            Width = Math.Min(620, Math.Max(1, workArea.Width - 48));
            MaxWidth = Width;
            MaxHeight = Math.Max(1, workArea.Height - 48);
            var layout = new Grid { Margin = new Thickness(20) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var symbol = CreateIcon(icon);
            if (symbol is not null) body.Children.Add(symbol);
            var text = new TextBox
            {
                Text = message,
                IsReadOnly = true,
                IsReadOnlyCaretVisible = false,
                TextWrapping = TextWrapping.Wrap,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            text.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            Grid.SetColumn(text, 1);
            body.Children.Add(text);
            var scroll = new ScrollViewer
            {
                Content = body,
                MaxHeight = Math.Max(1, MaxHeight - 160),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false
            };
            layout.Children.Add(scroll);

            var choices = GetChoices(buttons);
            var selectedDefault = choices.Contains(defaultResult) ? defaultResult : choices[0];
            var actions = new WrapPanel
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 20, 0, 0)
            };
            Button? defaultButton = null;
            foreach (var choice in choices)
            {
                var button = new Button
                {
                    Content = choice switch
                    {
                        MessageBoxResult.OK => "_OK",
                        MessageBoxResult.Yes => "_Yes",
                        MessageBoxResult.No => "_No",
                        _ => "_Cancel"
                    },
                    MinWidth = 88,
                    Margin = new Thickness(8, 0, 0, 0),
                    IsDefault = choice == selectedDefault
                };
                button.Click += (_, _) => Choose(choice);
                actions.Children.Add(button);
                if (button.IsDefault) defaultButton = button;
            }
            _defaultButton = defaultButton!;
            Grid.SetRow(actions, 1);
            layout.Children.Add(actions);
            Content = layout;

            SourceInitialized += (_, _) =>
            {
                if (_buttons == MessageBoxButton.YesNo)
                {
                    var menu = GetSystemMenu(new WindowInteropHelper(this).Handle, false);
                    if (menu != IntPtr.Zero) EnableMenuItem(menu, 0xF060, 0x00000001);
                }
            };
            Loaded += (_, _) =>
            {
                _defaultButton.Focus();
                PlaySound(icon);
            };
            PreviewKeyDown += (_, args) =>
            {
                if (args.Key != Key.Escape) return;
                args.Handled = true;
                if (_buttons != MessageBoxButton.YesNo) Choose(CloseResult);
            };
            Closing += (_, args) =>
            {
                if (Result != MessageBoxResult.None) return;
                if (_buttons == MessageBoxButton.YesNo) args.Cancel = true;
                else Result = CloseResult;
            };
        }

        private MessageBoxResult CloseResult => _buttons == MessageBoxButton.OK ? MessageBoxResult.OK : MessageBoxResult.Cancel;

        private void Choose(MessageBoxResult result)
        {
            Result = result;
            Close();
        }

        private static MessageBoxResult[] GetChoices(MessageBoxButton buttons) => buttons switch
        {
            MessageBoxButton.OK => [MessageBoxResult.OK],
            MessageBoxButton.OKCancel => [MessageBoxResult.OK, MessageBoxResult.Cancel],
            MessageBoxButton.YesNo => [MessageBoxResult.Yes, MessageBoxResult.No],
            _ => [MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel]
        };

        private static Rect GetWorkArea(Window? owner)
        {
            if (owner is null) return SystemParameters.WorkArea;
            var area = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(owner).Handle).WorkingArea;
            var scale = PresentationSource.FromVisual(owner)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            return new Rect(area.X * scale.M11, area.Y * scale.M22, area.Width * scale.M11, area.Height * scale.M22);
        }

        private static TextBlock? CreateIcon(MessageBoxImage icon)
        {
            if (icon == MessageBoxImage.None) return null;
            return new TextBlock
            {
                Text = icon switch
                {
                    MessageBoxImage.Error => "\u2716",
                    MessageBoxImage.Warning => "\u26A0",
                    MessageBoxImage.Question => "?",
                    _ => "\u24D8"
                },
                Foreground = icon switch
                {
                    MessageBoxImage.Error => new SolidColorBrush(Color.FromRgb(255, 172, 169)),
                    MessageBoxImage.Warning => new SolidColorBrush(Color.FromRgb(241, 197, 107)),
                    _ => new SolidColorBrush(Color.FromRgb(105, 226, 192))
                },
                FontFamily = new FontFamily("Segoe UI Symbol"),
                FontSize = 25,
                Margin = new Thickness(0, 0, 16, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
        }

        private static void PlaySound(MessageBoxImage icon)
        {
            // Match the existing native prompt's semantic notification sound.
            switch (icon)
            {
                case MessageBoxImage.Error: System.Media.SystemSounds.Hand.Play(); break;
                case MessageBoxImage.Warning: System.Media.SystemSounds.Exclamation.Play(); break;
                case MessageBoxImage.Question: System.Media.SystemSounds.Question.Play(); break;
                case MessageBoxImage.Information: System.Media.SystemSounds.Asterisk.Play(); break;
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetSystemMenu(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool revert);

        [DllImport("user32.dll")]
        private static extern uint EnableMenuItem(IntPtr menu, uint item, uint enable);
    }
}
