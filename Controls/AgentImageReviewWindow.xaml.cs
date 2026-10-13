using System.Windows;
using FullStackLauncher.ProjectTasks;

namespace FullStackLauncher.Controls;

public partial class AgentImageReviewWindow : Window
{
    private readonly CodexAgentBrowserContext? _context;
    public bool IncludePageContext => IncludeSourceChoice.IsChecked == true;

    public AgentImageReviewWindow(CodexAgentChatImage attachment, bool canEdit)
    {
        FullStackLauncher.Services.WindowTheme.Attach(this);
        InitializeComponent();
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        MaxHeight = SystemParameters.WorkArea.Height;
        CaptureTitle.Text = attachment.BrowserContext?.Title is { Length: > 0 } title ? title : attachment.Caption;
        CaptureImage.Source = CodexAgentImageStaging.CreatePreview(attachment, 1440);
        if (attachment.BrowserContext is { } context)
        {
            _context = context;
            IncludeSourceChoice.IsChecked = context.IncludePageContext;
            IncludeSourceChoice.IsEnabled = canEdit;
            CaptureSummary.Text = $"HTML {context.Html.Length:N0} characters · CSS {context.Css.Length:N0} characters · {context.Status}\nLarge source is shortened when sent; your message stays complete.";
        }
        else BrowserDetails.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = canEdit && attachment.BrowserContext is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SourceReview_Changed(object sender, RoutedEventArgs e)
    {
        if (_context is not null && SourceDetails.IsExpanded)
            SourceText.Text = (_context with { IncludePageContext = IncludePageContext }).FormatForReview();
    }

    private void Done_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
