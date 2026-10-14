using System.IO;
using System.Windows;
using FullStackLauncher.ProjectTasks;

namespace FullStackLauncher.Controls;

/// <summary>Inert screenshot review state; native capture handles never cross dashboard processes.</summary>
internal sealed record DesktopContextGathererResumeState
{
    public IReadOnlyList<CodexAgentChatImage> ConfirmedImages { get; init; } = [];
    public CodexAgentChatImage? PendingImage { get; init; }
    public int SelectedIndex { get; init; } = -1;
    public bool ReplacingPending { get; init; }
    public bool CaptureEnabled { get; init; }
    public int NextCaption { get; init; } = 1;
    public DesktopCaptureView CaptureView { get; init; }
    public double Left { get; init; }
    public double Top { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
}

public partial class DesktopContextGathererWindow
{
    private readonly Action<DesktopContextGathererResumeState>? _checkpointForReplacement;
    private DesktopContextGathererResumeState? _restoredDashboardResume;
    private bool _replacementPreparing;
    private bool _resumeCheckpointed;

    /// <summary>Drain an unfinished gesture, then preserve both accepted and unreviewed screenshots before closing.</summary>
    internal async Task<bool> PrepareDashboardResumeCloseAsync(CancellationToken cancellationToken)
    {
        Dispatcher.VerifyAccess();
        cancellationToken.ThrowIfCancellationRequested();
        if (_resumeCheckpointed) return true;
        if (_checkpointForReplacement is null || _replacementPreparing || _closeRequested ||
            _changingCaptureView || _restoringSource) return false;
        var resumeCapture = _captureEnabled;
        _replacementPreparing = true;
        _captureEnabled = false;
        UpdateView();
        try
        {
            // The capture backend drains real input releases itself. Never synthesize
            // input or discard the previous gallery while canceling an incomplete drag.
            var stopped = _captureStopped?.Task;
            _selectionCancellation?.Cancel();
            if (stopped is not null) await stopped.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (_capturing || _encoding || _changingCaptureView || _restoringSource || _closeRequested) return false;
            CloseSelectionWindow();
            RestoreController();
            var images = _pending is { } pending ? _confirmed.Append(pending).ToArray() : _confirmed.ToArray();
            CodexAgentImageStaging.ValidateImages(images.Select(item => item.Attachment).ToArray());
            var state = new DesktopContextGathererResumeState
            {
                ConfirmedImages = _confirmed.Select(item => item.Attachment).ToArray(),
                PendingImage = _pending?.Attachment,
                SelectedIndex = Array.IndexOf(images, _selectedCapture),
                ReplacingPending = _replacingPending,
                CaptureEnabled = resumeCapture,
                NextCaption = _nextCaption,
                CaptureView = _captureView,
                Left = Left,
                Top = Top,
                Width = ActualWidth,
                Height = ActualHeight
            };
            // The composer callback writes its encrypted checkpoint durably. A failed
            // write must throw and leave this window's normal close behavior intact.
            _checkpointForReplacement(state);
            _resumeCheckpointed = true;
            SetStatus("Screenshots saved for Launcher replacement.");
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            SetStatus("Screenshots could not be saved for Launcher replacement. Previous screenshots are kept; retry replacement.", true);
            throw new IOException("Context Gatherer resume state could not be saved. Existing screenshots were preserved.");
        }
        finally
        {
            _replacementPreparing = false;
            if (!_resumeCheckpointed && !_closeRequested)
            {
                _captureEnabled = resumeCapture;
                UpdateView();
                QueueNextCapture();
            }
        }
    }

    private void RestoreDashboardResume(DesktopContextGathererResumeState state)
    {
        var attachments = state.PendingImage is { } pending
            ? state.ConfirmedImages.Append(pending).ToArray() : state.ConfirmedImages.ToArray();
        CodexAgentImageStaging.ValidateImages(attachments);
        if (attachments.Length > _maximumCaptures ||
            attachments.Sum(image => (long)Convert.FromBase64String(image.DataBase64).Length) > _maximumBytes ||
            !Enum.IsDefined(state.CaptureView))
            throw new InvalidOperationException("Saved Context Gatherer state exceeds the remaining attachment limits. Existing saved screenshots were preserved.");
        // Freeze reconstructed previews, retaining the exact attachment bytes and
        // captured metadata. No capture source is reopened from stale native handles.
        var captures = attachments.Select(image => new CaptureItem(image,
            CodexAgentImageStaging.CreatePreview(image, 1440),
            Convert.FromBase64String(image.DataBase64).Length, null)).ToArray();
        foreach (var item in captures.Take(state.ConfirmedImages.Count)) _confirmed.Add(item);
        _pending = state.PendingImage is null ? null : captures.Last();
        _selectedCapture = state.SelectedIndex >= 0 && state.SelectedIndex < captures.Length
            ? captures[state.SelectedIndex] : _pending ?? _confirmed.LastOrDefault();
        _replacingPending = state.ReplacingPending && _pending is not null;
        _captureEnabled = state.CaptureEnabled;
        _nextCaption = Math.Max(1, state.NextCaption);
        _captureView = state.CaptureView;
        RestoreCaptureViewChoice();
        _restoredDashboardResume = state;
    }

    private void RestoreDashboardPlacement(DesktopContextGathererResumeState state)
    {
        if (!double.IsFinite(state.Left) || !double.IsFinite(state.Top) ||
            !double.IsFinite(state.Width) || !double.IsFinite(state.Height) ||
            state.Width <= 0 || state.Height <= 0) return;
        var area = SystemParameters.WorkArea;
        SizeToContent = SizeToContent.Manual;
        Width = Math.Clamp(state.Width, MinWidth, Math.Max(MinWidth, area.Width));
        Height = Math.Clamp(state.Height, MinHeight, Math.Max(MinHeight, area.Height));
        Left = Math.Clamp(state.Left, area.Left, Math.Max(area.Left, area.Right - Width));
        Top = Math.Clamp(state.Top, area.Top, Math.Max(area.Top, area.Bottom - Height));
    }
}
