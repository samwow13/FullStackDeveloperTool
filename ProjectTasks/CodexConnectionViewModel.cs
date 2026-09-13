using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Input;
using System.Windows.Threading;

namespace FullStackLauncher.ProjectTasks;

/// <summary>An explicit diagnostic operation; it never claims or advances queue items.</summary>
public sealed class CodexConnectionViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ProjectTaskStore _store = new();
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _projectId;
    private ProjectTaskData _data;
    private CodexModelOption? _model;
    private CodexReasoningEffortOption? _effort;
    private CodexConnectionCheck? _check;
    private CancellationTokenSource? _discoveryCancellation;
    private ConnectionReceiptRow? _selectedReceipt;
    private string? _activeAttempt;
    private bool _busy;
    private bool _stopping;
    private bool _reviewConfirmed;
    private bool _disposed;
    private string _status = "Refresh models, then choose a model and thinking level for the connection check.";

    public CodexConnectionViewModel(string projectId, string projectName, string folder)
    {
        _projectId = projectId;
        ProjectName = projectName;
        Folder = folder;
        _data = _store.Load();
        RefreshModelsCommand = new TaskPanelCommand(async () => await RefreshModelsAsync(), () => !IsBusy);
        RunCommand = new TaskPanelCommand(async () => await RunAsync(), () => CanRun);
        StopCommand = new TaskPanelCommand(async () => await StopAsync(), () => IsBusy && !_stopping);
        ReloadCommand = new TaskPanelCommand(Reload, () => !IsBusy);
        ConfirmAssociationCommand = new TaskPanelCommand(() => SetAssociation(CodexDesktopAssociation.ConfirmedByUser), () => CanRecordAssociation);
        NotVisibleCommand = new TaskPanelCommand(() => SetAssociation(CodexDesktopAssociation.NotVisibleToUser), () => CanRecordAssociation);
        RecordReviewCommand = new TaskPanelCommand(RecordReview, () => CanReview && ReviewConfirmed);
        RebuildHistory();
        if (_store.LoadWarning is { } warning) _status = warning;
    }

    public string ProjectName { get; }
    public string Folder { get; }
    public string Prompt => CodexConnectionCheck.Prompt;
    public string StorePath => _store.StorePath;
    public ObservableCollection<CodexModelOption> Models { get; } = [];
    public ObservableCollection<CodexReasoningEffortOption> Efforts { get; } = [];
    public ObservableCollection<ConnectionReceiptRow> History { get; } = [];
    public bool IsBusy => _busy;
    public bool CanConfigure => !IsBusy;
    public bool CanRun => !IsBusy && _store.CanSave && SelectedModel != null && SelectedEffort != null && !HasUnresolvedAttempt;
    public bool HasUnresolvedAttempt => _data.Receipts.Any(x => SameFolder(x.Snapshot.Folder, Folder) &&
        (x.Purpose == ProjectTaskExecutionPurpose.ConnectionCheck ? NeedsReview(x) : IsOutstanding(x)));
    public string HoldMessage => HasUnresolvedAttempt
        ? "An earlier attempt in this folder is unresolved. Check its exact task in Codex before running another check. Attempts from another project can be reviewed from that project's connection window."
        : "A check creates one Codex task using the fixed prompt below. It uses your Codex account and may consume usage. Queue items stay paused.";
    public string Status { get => _status; private set { _status = value; Refresh(); } }
    public bool CanRecordAssociation => !IsBusy && SelectedReceipt?.Receipt.ThreadId != null && _store.CanSave;
    public bool CanReview => !IsBusy && SelectedReceipt is { } row && NeedsReview(row.Receipt) && _store.CanSave;
    public bool ReviewConfirmed { get => _reviewConfirmed; set { _reviewConfirmed = value; Refresh(); } }
    public ConnectionReceiptRow? SelectedReceipt
    {
        get => _selectedReceipt;
        set { _selectedReceipt = value; _reviewConfirmed = false; Refresh(); }
    }
    public CodexModelOption? SelectedModel
    {
        get => _model;
        set
        {
            if (IsBusy || ReferenceEquals(_model, value)) return;
            _model = value;
            Efforts.Clear();
            if (value != null)
                foreach (var option in value.SupportedReasoningEfforts) Efforts.Add(option);
            _effort = Efforts.FirstOrDefault(x => x.Id == value?.DefaultReasoningEffort);
            Refresh();
        }
    }
    public CodexReasoningEffortOption? SelectedEffort
    {
        get => _effort;
        set { if (!IsBusy) { _effort = value; Refresh(); } }
    }

    public ICommand RefreshModelsCommand { get; }
    public ICommand RunCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ReloadCommand { get; }
    public ICommand ConfirmAssociationCommand { get; }
    public ICommand NotVisibleCommand { get; }
    public ICommand RecordReviewCommand { get; }

    private async Task RefreshModelsAsync()
    {
        _busy = true;
        Status = "Connecting to Codex and reading available models…";
        using var discovery = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _discoveryCancellation = discovery;
        try
        {
            var models = await new CodexModelCatalog().LoadAsync(discovery.Token);
            Models.Clear();
            foreach (var model in models) Models.Add(model);
            // Preserve an explicit choice on refresh, otherwise use the advertised default.
            var previous = _model?.Id;
            _busy = false;
            SelectedModel = Models.FirstOrDefault(x => x.Id == previous) ?? Models.FirstOrDefault(x => x.IsDefault) ?? Models.FirstOrDefault();
            Status = $"Connected. {Models.Count} models discovered. This confirms discovery only; run the check to verify a turn.";
        }
        catch (OperationCanceledException) { Status = "Model discovery stopped."; }
        catch (Exception ex) { Status = SafeError(ex); Models.Clear(); _model = null; _effort = null; Efforts.Clear(); }
        finally { _discoveryCancellation = null; _busy = false; _stopping = false; Refresh(); }
    }

    private async Task RunAsync()
    {
        if (!CanRun) return;
        _busy = true;
        _stopping = false;
        _activeAttempt = null;
        Mutex? owner = null;
        var ownsLock = false;
        try
        {
            if (!Path.IsPathFullyQualified(Folder) || !Directory.Exists(Folder))
                throw new InvalidOperationException("The assigned folder is unavailable. Update the queue's folder explicitly, then reopen this window.");
            // This bounded, read-only check has a folder owner across launcher copies.
            // The future tray coordinator still needs its own lifetime owner and checkout exclusion.
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Folder)).ToUpperInvariant())));
            owner = new Mutex(false, "Local\\FullStackLauncher.CodexConnection." + key);
            try { ownsLock = owner.WaitOne(0); }
            catch (AbandonedMutexException) { ownsLock = true; }
            if (!ownsLock) throw new InvalidOperationException("Another launcher is checking this folder. Wait for that check to finish.");

            _data = _store.Load();
            if (!_store.CanSave) throw new InvalidOperationException(_store.LoadWarning);
            if (HasUnresolvedAttempt) throw new InvalidOperationException(HoldMessage);
            var now = DateTimeOffset.UtcNow;
            var id = Guid.NewGuid().ToString("N");
            var receipt = new ProjectTaskExecutionReceipt
            {
                AttemptId = id,
                Purpose = ProjectTaskExecutionPurpose.ConnectionCheck,
                CreatedAt = now,
                UpdatedAt = now,
                Snapshot = new()
                {
                    ProjectId = _projectId, ProjectName = ProjectName,
                    NoteId = "connection-check/" + id, QueueItemId = "connection-check/" + id,
                    Name = "Launcher connection check", Prompt = Prompt,
                    ModelId = _model!.Id, ReasoningEffort = _effort!.Id, Folder = Path.GetFullPath(Folder)
                },
                ResultSummary = "Prepared locally. If the launcher exits before a result is saved, review this attempt before trying again."
            };
            var candidate = ProjectTaskStore.Clone(_data);
            candidate.Receipts.Add(receipt);
            _store.Save(candidate); // Durable intent precedes every external submission.
            _data = candidate;
            _activeAttempt = id;
            RebuildHistory(id);
            _check = new CodexConnectionCheck();
            Status = "Checking model access before submitting the fixed connection prompt…";
            var result = await _check.CheckAsync(receipt.Snapshot.Folder, receipt.Snapshot.ModelId,
                receipt.Snapshot.ReasoningEffort, PersistUpdateAsync, _lifetime.Token);
            SaveUpdate(new CodexCheckUpdate(result.State, result.Folder, result.ModelId, result.ReasoningEffort,
                result.ThreadId, result.TurnId, result.Summary, result.InstructionSourcePaths, result.TerminalConfirmed));
            Status = result.State == CodexCheckState.Completed
                ? "Connection check completed. Verify this exact task's location in the Codex desktop app and record what you observe below. Automatic execution remains unavailable."
                : result.Summary;
        }
        catch (Exception ex)
        {
            var message = SafeError(ex);
            if (_activeAttempt != null)
            {
                try
                {
                    MutateReceipt(_activeAttempt, receipt =>
                    {
                        // A later local failure cannot erase an already saved terminal result.
                        if (receipt.FinishedAt != null) return;
                        receipt.ThreadId ??= _check?.ThreadId;
                        receipt.TurnId ??= _check?.TurnId;
                        // A local failure is not evidence that an external submission never arrived.
                        receipt.State = ProjectTaskRunState.Recovering;
                        receipt.Outcome = ProjectTaskOutcome.Unknown;
                        receipt.AttentionReason = message;
                        receipt.ResultSummary = "No confirmed result. Review the recorded task in Codex; this attempt will not be resent.";
                    });
                }
                catch (Exception)
                {
                    message += " The latest result could not be saved. The earlier intent remains unresolved; reload and review it before another check.";
                }
            }
            Status = message;
        }
        finally
        {
            _check = null;
            _busy = false;
            _stopping = false;
            if (ownsLock) owner!.ReleaseMutex();
            owner?.Dispose();
            RebuildHistory(_activeAttempt);
            Refresh();
        }
    }

    private Task PersistUpdateAsync(CodexCheckUpdate update) => _dispatcher.InvokeAsync(() =>
    {
        SaveUpdate(update);
        Status = update.Summary;
    }).Task;

    private void SaveUpdate(CodexCheckUpdate update)
    {
        if (_activeAttempt == null) throw new InvalidOperationException("The connection check has no saved intent.");
        MutateReceipt(_activeAttempt, receipt =>
        {
            receipt.ThreadId ??= update.ThreadId;
            receipt.TurnId ??= update.TurnId;
            if ((update.ThreadId != null && receipt.ThreadId != update.ThreadId) ||
                (update.TurnId != null && receipt.TurnId != update.TurnId))
                throw new InvalidOperationException("Codex returned an unexpected task identity. This check is held for review.");
            receipt.ResultSummary = update.Summary;
            if (update.State == CodexCheckState.Starting) receipt.SubmissionStartedAt ??= DateTimeOffset.UtcNow;
            if (update.State == CodexCheckState.Running) receipt.StartedAt ??= DateTimeOffset.UtcNow;
            receipt.State = update.State switch
            {
                CodexCheckState.Starting or CodexCheckState.ThreadCreated => ProjectTaskRunState.Starting,
                CodexCheckState.Running => ProjectTaskRunState.Running,
                CodexCheckState.Completed => ProjectTaskRunState.Completed,
                CodexCheckState.Interrupted => ProjectTaskRunState.Interrupted,
                CodexCheckState.Failed => ProjectTaskRunState.Failed,
                CodexCheckState.Blocked or CodexCheckState.NeedsInput => ProjectTaskRunState.NeedsAttention,
                _ => ProjectTaskRunState.Recovering
            };
            receipt.Outcome = update.State switch
            {
                CodexCheckState.Completed => ProjectTaskOutcome.Succeeded,
                CodexCheckState.Blocked => ProjectTaskOutcome.Blocked,
                CodexCheckState.NeedsInput => ProjectTaskOutcome.NeedsInput,
                CodexCheckState.Interrupted => ProjectTaskOutcome.Interrupted,
                CodexCheckState.Failed => ProjectTaskOutcome.Failed,
                _ => ProjectTaskOutcome.Unknown
            };
            if (update.TerminalConfirmed)
                receipt.FinishedAt ??= DateTimeOffset.UtcNow;
            if (update.State is CodexCheckState.Unknown or CodexCheckState.Blocked or CodexCheckState.NeedsInput)
                receipt.AttentionReason = update.Summary;
            if (update.ThreadId != null)
                receipt.ConnectionDetails = $"Resolved folder: {update.Folder}\nResolved model: {update.ModelId}\nResolved thinking level: {update.ReasoningEffort}\nInstruction sources reported by Codex:\n" +
                    (update.InstructionSourcePaths.Count == 0 ? "No source paths reported; instruction loading is unverified." : string.Join("\n", update.InstructionSourcePaths));
        });
    }

    public async Task StopAsync()
    {
        if (!IsBusy || _stopping) return;
        _stopping = true;
        Status = "Stop requested. Waiting for Codex to confirm interruption; a lost connection will remain unresolved.";
        try
        {
            if (_check != null) await _check.InterruptAsync();
            else _discoveryCancellation?.Cancel();
        }
        catch (Exception ex) { Status = SafeError(ex) + " Waiting for the bounded check to finish or report an uncertain result."; }
        Refresh();
    }

    private void MutateReceipt(string attemptId, Action<ProjectTaskExecutionReceipt> mutation)
    {
        // Read fresh data to preserve note edits from another window. A race at Save
        // fails closed; it must never cause a second external submission.
        var candidate = _store.Load();
        if (!_store.CanSave) throw new InvalidOperationException(_store.LoadWarning);
        var receipt = candidate.Receipts.Single(x => x.AttemptId == attemptId && x.Purpose == ProjectTaskExecutionPurpose.ConnectionCheck);
        mutation(receipt);
        receipt.UpdatedAt = DateTimeOffset.UtcNow < receipt.CreatedAt ? receipt.CreatedAt : DateTimeOffset.UtcNow;
        _store.Save(candidate);
        _data = candidate;
        RebuildHistory(attemptId);
    }

    private void SetAssociation(CodexDesktopAssociation association)
    {
        if (!CanRecordAssociation) return;
        try
        {
            MutateReceipt(SelectedReceipt!.Receipt.AttemptId, receipt => receipt.DesktopAssociation = association);
            Status = "Desktop observation saved for this attempt. It does not enable queue execution or prove future sidebar placement.";
        }
        catch (Exception ex) { Status = SafeError(ex); }
    }

    private void RecordReview()
    {
        if (!CanReview || !ReviewConfirmed) return;
        try
        {
            MutateReceipt(SelectedReceipt!.Receipt.AttemptId, receipt => receipt.ConnectionReviewCompletedAt = DateTimeOffset.UtcNow);
            Status = "Manual review recorded. The original outcome and IDs are retained. A new check requires clicking Run connection check again.";
        }
        catch (Exception ex) { Status = SafeError(ex); }
    }

    private void Reload()
    {
        _data = _store.Load();
        RebuildHistory(SelectedReceipt?.Receipt.AttemptId);
        Status = _store.LoadWarning ?? "Saved connection checks reloaded. An unfinished attempt is unresolved until reviewed in Codex.";
    }

    private void RebuildHistory(string? selectId = null)
    {
        selectId ??= SelectedReceipt?.Receipt.AttemptId;
        History.Clear();
        foreach (var receipt in _data.Receipts.Where(x => x.Purpose == ProjectTaskExecutionPurpose.ConnectionCheck && x.Snapshot.ProjectId == _projectId)
                     .OrderByDescending(x => x.CreatedAt))
            History.Add(new(receipt, IsBusy && receipt.AttemptId == _activeAttempt));
        _selectedReceipt = History.FirstOrDefault(x => x.Receipt.AttemptId == selectId) ?? History.FirstOrDefault();
        _reviewConfirmed = false;
        Refresh();
    }

    internal static bool IsOutstanding(ProjectTaskExecutionReceipt receipt) => receipt.State is ProjectTaskRunState.Prepared or ProjectTaskRunState.Starting or ProjectTaskRunState.Running or ProjectTaskRunState.Recovering or ProjectTaskRunState.NeedsAttention;
    internal static bool NeedsReview(ProjectTaskExecutionReceipt receipt) => receipt.ConnectionReviewCompletedAt == null &&
        (IsOutstanding(receipt) || (receipt.TurnId != null && receipt.FinishedAt == null));
    private static bool SameFolder(string first, string second)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return false; }
    }
    private static string SafeError(Exception error) => error switch
    {
        InvalidOperationException or TimeoutException => error.Message,
        OperationCanceledException => "The check stopped without a confirmed terminal result. Review any saved attempt before running another check.",
        _ => "The connection check could not finish or save its result. Check Codex setup and task-store access, then reload the saved attempt."
    };
    private void Refresh()
    {
        if (_disposed) return;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
        CommandManager.InvalidateRequerySuggested();
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Dispose() { _disposed = true; _lifetime.Cancel(); _lifetime.Dispose(); }
}

public sealed record ConnectionReceiptRow(ProjectTaskExecutionReceipt Receipt, bool IsLive)
{
    public string Label => $"{Receipt.CreatedAt.LocalDateTime:g} · {StateLabel}";
    public string StateLabel => !IsLive && CodexConnectionViewModel.NeedsReview(Receipt) ? "Needs review" : Receipt.State.ToString();
    public string Identity => $"Task: {Receipt.ThreadId ?? "not acknowledged"}\nTurn: {Receipt.TurnId ?? "not acknowledged"}\nAttempt: {Receipt.AttemptId}";
    public string Requested => $"{Receipt.Snapshot.ProjectName}\n{Receipt.Snapshot.Folder}\nRequested: {Receipt.Snapshot.ModelId} · {Receipt.Snapshot.ReasoningEffort}";
    public string Evidence => $"Submitted: {Format(Receipt.SubmissionStartedAt)}\nStart acknowledged: {Format(Receipt.StartedAt)}\nTerminal recorded: {Format(Receipt.FinishedAt)}\nReported outcome: {Receipt.Outcome}\nManual hold review: {Format(Receipt.ConnectionReviewCompletedAt)}";
    public string Association => Receipt.DesktopAssociation switch
    {
        CodexDesktopAssociation.ConfirmedByUser => "Desktop project: manually confirmed for this task",
        CodexDesktopAssociation.NotVisibleToUser => "Desktop project: user could not find this task",
        _ => "Desktop project: unverified"
    };
    private static string Format(DateTimeOffset? value) => value?.LocalDateTime.ToString("g") ?? "not recorded";
}
