using System.IO;
using System.Windows;
using FullStackLauncher.ProjectTasks;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class MainWindow
{
    private CancellationTokenSource? _waitingAgentQueueCancellation;
    private CodexAgentWaitingQueue? _waitingAgentQueue;
    private IReadOnlySet<string> _waitingAgentQueueIds = new HashSet<string>(StringComparer.Ordinal);
    private bool _waitingAgentQueueLoaded;
    private bool _loadingWaitingAgentQueue;
    private bool _stoppingWaitingAgentQueue;
    private string? _waitingAgentQueueError;
    private string _waitingAgentQueueStatus = "Checking saved queue…";

    public bool HasWaitingAgentQueue => _waitingAgentQueue?.OwnsPrompts == true || _waitingAgentQueueCancellation is not null;
    public bool CanStopWaitingAgentQueue => _waitingAgentQueue?.OwnsPrompts == true && !_loadingWaitingAgentQueue &&
        !_stoppingWaitingAgentQueue && !_closed && !_closing && !_closeRequested;

    private void ApplyWaitingAgentQueue(CodexAgentWaitingQueue? queue)
    {
        // A completed background write must not overwrite a later explicit Stop.
        if (queue is not null && _waitingAgentQueue?.Id == queue.Id && _waitingAgentQueue.Revision > queue.Revision) return;
        _waitingAgentQueue = queue;
        _waitingAgentQueueIds = queue is { OwnsPrompts: true }
            ? queue.Entries.Skip(queue.Index).Select(entry => entry.WaitingId).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        _waitingAgentQueueStatus = queue is { State: CodexAgentWaitingQueueState.NeedsReview }
            ? "Queue needs review" : queue is { State: CodexAgentWaitingQueueState.Active }
            ? $"Queue {queue.Index + 1}/{queue.Entries.Count} · {(queue.DispatchStarted ? "waiting for current agent" : "ready for next agent")}" : "";
        UpdateWaitingAgentActions();
    }

    private async Task RestoreWaitingAgentQueueAsync()
    {
        if (_waitingAgentQueueLoaded || _loadingWaitingAgentQueue || _closed) return;
        _loadingWaitingAgentQueue = true;
        UpdateWaitingAgentActions();
        try
        {
            var queue = await Task.Run(_waitingAgentStore.ReadQueue);
            if (_closed) return;
            ApplyWaitingAgentQueue(queue);
            _waitingAgentQueueError = null;
            _waitingAgentQueueLoaded = true;
            if (queue is { State: CodexAgentWaitingQueueState.NeedsReview }) Notice = queue.Summary;
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                _waitingAgentQueueError = SensitiveDataProtection.Redact(ex.Message);
                Notice = "Saved queue could not be restored. Prompts and receipts are kept. " + _waitingAgentQueueError;
            }
        }
        finally
        {
            _loadingWaitingAgentQueue = false;
            if (!_closed) UpdateWaitingAgentActions();
        }
        if (!_closed && _waitingAgentQueueLoaded && _waitingAgentQueue is { State: CodexAgentWaitingQueueState.Active })
            _ = RunWaitingAgentQueueAsync();
    }

    private async void StopWaitingAgentQueue_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStopWaitingAgentQueue) return;
        var queueId = _waitingAgentQueue!.Id;
        _stoppingWaitingAgentQueue = true;
        UpdateWaitingAgentActions();
        try
        {
            var stopped = await Task.Run(() => _waitingAgentStore.StopQueue(queueId));
            ApplyWaitingAgentQueue(stopped);
            _waitingAgentQueueError = null;
            _waitingAgentQueueCancellation?.Cancel();
            Notice = stopped.Summary;
        }
        catch (Exception ex)
        {
            // Do not keep dispatching when Stop failed to save. The explicit retry
            // remains available; never claim a restart-safe stop without its record.
            _waitingAgentQueueCancellation?.Cancel();
            _waitingAgentQueueStatus = "Stop not saved · retry Stop queue";
            Notice = "Queue paused here, but Stop could not be saved and it may resume after restart. Retry Stop queue. " +
                SensitiveDataProtection.Redact(ex.Message);
        }
        finally
        {
            _stoppingWaitingAgentQueue = false;
            if (!_closed)
            {
                await RefreshWaitingAgentsAsync();
                UpdateWaitingAgentActions();
            }
        }
    }

    private async void QueueWaitingAgents_Click(object sender, RoutedEventArgs e)
    {
        if (!CanReleaseWaitingAgents) return;
        _releasingWaitingAgents = true;
        UpdateWaitingAgentActions();
        try
        {
            // Later Wait saves stay outside this durable, explicitly chosen batch.
            var batch = (await Task.Run(() => _waitingAgentStore.List())).Where(item => item.IsWaiting).ToArray();
            foreach (var item in batch) ValidateWaitingAgentProject(item);
            if (batch.Length == 0) return;
            ApplyWaitingAgentQueue(await Task.Run(() => _waitingAgentStore.StartQueue(batch)));
        }
        catch (Exception ex)
        {
            Notice = "Queue could not start. Saved prompts are kept. " + SensitiveDataProtection.Redact(ex.Message);
        }
        finally
        {
            _releasingWaitingAgents = false;
            await RefreshWaitingAgentsAsync();
            UpdateWaitingAgentActions();
        }
        if (!_closed && _waitingAgentQueue is { State: CodexAgentWaitingQueueState.Active })
            _ = RunWaitingAgentQueueAsync();
    }

    private async Task RunWaitingAgentQueueAsync()
    {
        if (_waitingAgentQueueCancellation is not null || _waitingAgentQueue is not { State: CodexAgentWaitingQueueState.Active } initial) return;
        using var cancellation = new CancellationTokenSource();
        _waitingAgentQueueCancellation = cancellation;
        FileStream? owner = null;
        UpdateWaitingAgentActions();
        try
        {
            // Dashboard handoff waits for the previous process to exit. This extra
            // exact-scope claim prevents a second observer from dispatching a batch.
            owner = await Task.Run(_waitingAgentStore.ClaimQueueOwner, cancellation.Token);
            while (_waitingAgentQueue is { State: CodexAgentWaitingQueueState.Active } queue)
            {
                await WaitForWaitingAgentQueueDispatchAsync(cancellation.Token);
                if (!queue.DispatchStarted)
                {
                    var item = await Task.Run(() => _waitingAgentStore.ReadQueueItem(queue), cancellation.Token);
                    await WaitForWaitingAgentQueueDispatchAsync(cancellation.Token);
                    ValidateWaitingAgentProject(item);
                    _releasingWaitingAgents = true;
                    UpdateWaitingAgentActions();
                    try
                    {
                        queue = await Task.Run(() => _waitingAgentStore.BeginQueueDispatch(queue));
                        ApplyWaitingAgentQueue(queue);
                        _waitingAgentQueueStatus = $"Queue {queue.Index + 1}/{queue.Entries.Count} · starting {queue.Current.ProjectName}…";
                        UpdateWaitingAgentActions();
                        // After durable intent, finish submission checks. Stop and
                        // closure never cancel, recreate, or resend this exact owner.
                        var receipt = await _waitingAgentStore.ReleaseQueuedAsync(queue);
                        if (!receipt.IsAccepted)
                            throw new InvalidOperationException($"{queue.Current.ProjectName}: {receipt.Summary}");
                    }
                    finally
                    {
                        _releasingWaitingAgents = false;
                        await RefreshWaitingAgentsAsync();
                        UpdateWaitingAgentActions();
                    }
                }
                // On restart this is the only path for an already released entry.
                // Missing receipts, lost owners, failed turns, and uncertain starts
                // cannot become a new submission or permit the next prompt.
                _waitingAgentQueueStatus = $"Queue {queue.Index + 1}/{queue.Entries.Count} · waiting for {queue.Current.ProjectName} to finish";
                UpdateWaitingAgentActions();
                cancellation.Token.ThrowIfCancellationRequested();
                if (!await Task.Run(() => _waitingAgentStore.IsQueueAgentFinished(queue), cancellation.Token))
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellation.Token);
                    continue;
                }
                await WaitForWaitingAgentQueueDispatchAsync(cancellation.Token);
                _releasingWaitingAgents = true;
                UpdateWaitingAgentActions();
                try { ApplyWaitingAgentQueue(await Task.Run(() => _waitingAgentStore.AdvanceQueue(queue))); }
                finally { _releasingWaitingAgents = false; }
                await RefreshWaitingAgentsAsync();
            }
            if (!_closed && _waitingAgentQueue is { State: CodexAgentWaitingQueueState.Completed } completed)
                Notice = completed.Summary;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Closing cancels this observer only. Active persisted state resumes
            // automatically when the next dashboard has finished loading.
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                try
                {
                    // Failure to claim an owner must not stop another live owner.
                    if (owner is not null)
                        ApplyWaitingAgentQueue(await Task.Run(() => _waitingAgentStore.PauseQueueForReview(initial.Id, ex.Message)));
                    Notice = _waitingAgentQueue?.Summary is { Length: > 0 } summary ? summary
                        : "Queue could not continue. " + SensitiveDataProtection.Redact(ex.Message);
                }
                catch (Exception saveError)
                {
                    _waitingAgentQueueError = SensitiveDataProtection.Redact(saveError.Message);
                    Notice = "Queue paused here, but its review state could not be saved. Prompts and receipts are kept. " +
                        SensitiveDataProtection.Redact(ex.Message) + " " + _waitingAgentQueueError;
                }
            }
        }
        finally
        {
            owner?.Dispose();
            _releasingWaitingAgents = false;
            _waitingAgentQueueCancellation = null;
            if (!_closed)
            {
                await RefreshWaitingAgentsAsync();
                UpdateWaitingAgentActions();
            }
        }
    }

    private async Task WaitForWaitingAgentQueueDispatchAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_closed) throw new OperationCanceledException(cancellationToken);
            if (_waitingAgentError is not null)
                throw new InvalidOperationException("Waiting prompts could not be read. " + _waitingAgentError);
            // Never dispatch inside close confirmations, startup checks, or editors.
            if (!_closing && !_closeRequested && !_checkingStartupServices && !_savingProjectEdits &&
                !_loadingWaitingAgents && !_stoppingWaitingAgentQueue && _startAgentWindow is null &&
                _waitingAgentsWindow is null && IsEnabled) return;
            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
        }
    }
}
