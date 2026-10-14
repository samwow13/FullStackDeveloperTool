using System.Windows;
using FullStackLauncher.ProjectTasks;
using FullStackLauncher.Services;

namespace FullStackLauncher.Controls;

public partial class CodexStartAgentWindow
{
    private readonly CodexAgentWaitingStore _waitingStore = new();
    private bool _savingWaiting;

    public int WaitingSavedCount { get; private set; }
    public event EventHandler? WaitingSaved;

    private async void Wait_Click(object sender, RoutedEventArgs e)
    {
        if (!WaitButton.IsEnabled || !TryCaptureRequest(out var request, out var preferences)) return;
        _savingWaiting = true;
        IsSubmitting = true;
        ShowStatus("Saving prompt to Waiting…");
        UpdateControls();
        var saved = false;
        var readyForNextCapture = false;
        try
        {
            await StopContextGathererAsync();
            // Context is frozen with this prompt; only Save context changes the project default.
            _preferences = await Task.Run(() => _store.Save(preferences));
            await Task.Run(() => _waitingStore.Save(request));
            WaitingSavedCount++;
            saved = true;
            PromptBox.Clear();
            var undoEnabled = PromptBox.IsUndoEnabled;
            PromptBox.IsUndoEnabled = false;
            PromptBox.IsUndoEnabled = undoEnabled;
            PromptBox.CaretIndex = 0;
            PromptBox.ScrollToHome();
            ClearMessageImages();
            // Keep the chosen model, thinking, access, speed, and Keep awake for the next prompt.
            ShowStatus("Saved to Waiting. Capture screenshots for the next prompt, or close Context Gatherer to type.");
            readyForNextCapture = true;
        }
        catch (Exception ex)
        {
            if (!_closed) ShowStatus(saved
                ? "Prompt saved to Waiting. The composer could not reset. " + SensitiveDataProtection.Redact(ex.Message)
                : "Prompt could not be saved. Your message is kept. " + SensitiveDataProtection.Redact(ex.Message), error: true);
        }
        finally
        {
            _savingWaiting = false;
            IsSubmitting = false;
            if (!_closed)
            {
                UpdateControls();
                PromptBox.Focus();
            }
        }
        if (saved)
        {
            try { WaitingSaved?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex)
            {
                if (!_closed) ShowStatus("Prompt saved to Waiting. Close this window to refresh the waiting list. " +
                    SensitiveDataProtection.Redact(ex.Message), error: true);
                return;
            }
        }
        // The frozen waiting entry is already durable. Only a fully reset draft
        // opens a fresh gather; capture failure or cancellation cannot undo it.
        if (readyForNextCapture && !_closed)
        {
            try { await OpenContextGathererAsync(); }
            catch (Exception ex)
            {
                if (!_closed) ShowStatus("Prompt saved to Waiting. Context Gatherer could not reopen. " +
                    SensitiveDataProtection.Redact(ex.Message), error: true);
            }
        }
    }
}
