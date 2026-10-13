using System.Windows;
using FullStackLauncher.ProjectTasks;
using FullStackLauncher.Services;

namespace FullStackLauncher.Controls;

public partial class CodexStartAgentWindow
{
    private CancellationTokenSource? _contextGathererLifetime;
    private bool _changingContextGatherer;
    private bool _receivingContextGathererDraft;

    private void UpdateContextGathererControls()
    {
        ContextGathererButton.IsEnabled = !IsSubmitting && !_checkingStatus && !_savingContext && !_pastingImage &&
            !_closingContext && !_closed && PendingAttemptId is null && _composerDraft is null &&
            !_changingContextGatherer && !_receivingContextGathererDraft;
        ContextGathererButton.Content = _changingContextGatherer ? "Gathering…" : "Context _Gatherer";
        ContextGathererStatus.Text = _changingContextGatherer ? "Capture, review, then write your prompt" : "Any app · Ctrl+drag to capture";
    }

    private async void ContextGatherer_Click(object sender, RoutedEventArgs e)
    {
        if (!ContextGathererButton.IsEnabled) return;
        var existing = DraftImages;
        var remainingCount = CodexAgentChatImage.MaximumCount - existing.Count;
        if (remainingCount <= 0)
        {
            ShowStatus("Attach at most 12 images. Remove an image before gathering another.", error: true);
            return;
        }
        _changingContextGatherer = true;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _contextGathererLifetime = lifetime;
        UpdateControls();
        // Minimizing preserves ShowDialog's lifetime and the dashboard's saved draft owner.
        // The floating gatherer is unowned so it remains available while these windows minimize.
        var windows = Application.Current.Windows.Cast<Window>().Where(window => window.IsVisible)
            .OrderBy(WindowOwnerDepth)
            .Select(window => (Window: window, State: window.WindowState)).ToArray();
        var wasEnabled = IsEnabled;
        try
        {
            var remainingBytes = await Task.Run(() =>
            {
                CodexAgentImageStaging.ValidateImages(existing);
                return CodexAgentChatImage.MaximumTotalBytes - existing.Sum(image => Convert.FromBase64String(image.DataBase64).Length);
            }, lifetime.Token);
            if (remainingBytes <= 0)
            {
                ShowStatus("Attached images already use the 24 MB limit. Remove an image before gathering another.", error: true);
                return;
            }
            IsEnabled = false;
            foreach (var (window, _) in windows.Reverse()) window.WindowState = WindowState.Minimized;
            var captured = await DesktopContextGathererWindow.GatherAsync(this, remainingCount, remainingBytes, lifetime.Token);
            if (_closed || captured is not { Count: > 0 }) return;
            _receivingContextGathererDraft = true;
            var prepared = await Task.Run(() =>
            {
                CodexAgentImageStaging.ValidateImages(existing.Concat(captured).ToArray());
                return captured.Select(image => new MessageImage(image, MakePreview(image))).ToArray();
            });
            if (_closed) return;
            foreach (var image in prepared) _messageImages.Add(image);
            UpdateMessageImages();
            ShowStatus($"{prepared.Length} screenshot{(prepared.Length == 1 ? "" : "s")} added. Describe the change, then choose Start Agent.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_closed) ShowStatus("Context Gatherer could not finish. Your existing message and images are kept. " +
                SensitiveDataProtection.Redact(ex.Message), error: true);
        }
        finally
        {
            _contextGathererLifetime = null;
            // Restore ancestors before their owned windows, without reopening closed windows.
            var open = Application.Current.Windows.Cast<Window>().ToHashSet();
            var restoreFailed = false;
            foreach (var (window, state) in windows)
            {
                if (!open.Contains(window)) continue;
                try { window.WindowState = state; }
                catch (InvalidOperationException) { restoreFailed = true; }
            }
            _changingContextGatherer = false;
            _receivingContextGathererDraft = false;
            if (!_closed)
            {
                IsEnabled = wasEnabled;
                UpdateControls();
                try
                {
                    if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                    Activate();
                    PromptBox.Focus();
                    PromptBox.CaretIndex = PromptBox.Text.Length;
                }
                catch (InvalidOperationException) { restoreFailed = true; }
                if (restoreFailed) ShowStatus("A Launcher window could not be restored. Your draft is kept; switch back to Start Agent to continue.", error: true);
            }
        }
    }

    private static int WindowOwnerDepth(Window window)
    {
        var depth = 0;
        for (var owner = window.Owner; owner is not null; owner = owner.Owner) depth++;
        return depth;
    }

    private Task StopContextGathererAsync()
    {
        _contextGathererLifetime?.Cancel();
        return Task.CompletedTask;
    }
}
