using System.Windows;
using System.Windows.Media;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class GitReleaseBranchWindow
{
    private enum MergeFlowStage { Source, Comparing, Preview, Review, Merging, Finished }
    private MergeFlowStage _mergeFlowStage;
    private GitLocalMergeReview? _mergeReview;

    private void UpdateMergeFlowAvailability()
    {
        if (_compare == null || MergeFlowPanel == null || CancelButton == null) return;
        var hasPreview = _mergeReview != null;
        var canMerge = hasPreview && _mergeReview!.Comparison.IncomingCommits > 0 && _merge != null;
        var source = _mergeFlowStage == MergeFlowStage.Source;
        Headline.Visibility = source ? Visibility.Collapsed : Visibility.Visible;
        BranchPicker.Visibility = source && !_showingCachedSource ? Visibility.Visible : Visibility.Collapsed;
        MergeFlowPanel.Visibility = hasPreview || _mergeFlowStage == MergeFlowStage.Finished ? Visibility.Visible : Visibility.Collapsed;
        MergeApprovalPanel.Visibility = _mergeFlowStage == MergeFlowStage.Review ? Visibility.Visible : Visibility.Collapsed;
        if (!source)
        {
            Submit.Content = _mergeFlowStage == MergeFlowStage.Review ? "_Merge locally" : "Review local merge…";
            Submit.IsEnabled = !_loading && !_closing && UnconfirmedCommand == null && canMerge
                && _mergeFlowStage is MergeFlowStage.Preview or MergeFlowStage.Review;
            Submit.Visibility = canMerge && _mergeFlowStage != MergeFlowStage.Finished ? Visibility.Visible : Visibility.Collapsed;
            Retry.Content = _mergeFlowStage == MergeFlowStage.Review ? "Back to comparison" : "Change source…";
            Retry.Visibility = !_loading && !_closing && UnconfirmedCommand == null
                && _mergeFlowStage is MergeFlowStage.Preview or MergeFlowStage.Review ? Visibility.Visible : Visibility.Collapsed;
        }
        else Submit.Visibility = Visibility.Visible;
        CancelButton.Content = _loading ? "Cancel operation"
            : _mergeFlowStage is MergeFlowStage.Preview or MergeFlowStage.Review or MergeFlowStage.Finished ? "Back to Git" : "Cancel";
        CancelButton.IsEnabled = !_closing && !(_loading && _loadCancellation?.IsCancellationRequested == true);
    }

    private async Task<bool> TryAdvanceMergeFlowAsync()
    {
        if (_compare == null || _mergeFlowStage == MergeFlowStage.Source) return false;
        if (!Submit.IsEnabled || _loading || _closing) return true;
        if (_mergeFlowStage == MergeFlowStage.Preview && _mergeReview is { } review)
        {
            _mergeFlowStage = MergeFlowStage.Review;
            Headline.Text = "Approve local merge";
            var comparison = review.Comparison;
            var changedFiles = review.Snapshot.Changes.Count;
            Description.Text = "Review local changes before merging.";
            MergeApprovalScope.Text = (changedFiles > 0
                ? $"First recheck and fetch the exact reviewed source. If the branch or source changed, stop before checkpointing or merging. Stage all {changedFiles:N0} changed files, including additions, modifications and deletions, and save a protective LOCAL checkpoint on the current branch before merging. "
                : "First recheck and fetch the exact reviewed source. If the branch or source changed, stop before merging. The reviewed working tree is clean, so no protective checkpoint is needed. ")
                + (comparison.HasConflicts ? "The comparison found conflicts; approving starts a local merge that needs manual resolution. "
                    : "The comparison found no conflicts between the committed branches. Uncommitted edits can still produce conflicts. ")
                + "The merge remains local and its merge commit waits for review. Nothing is pushed. Ignored untracked files are excluded. Submodule contents must be committed separately; only the submodule reference can be recorded.";
            CheckpointPanel.Visibility = changedFiles > 0 ? Visibility.Visible : Visibility.Collapsed;
            CheckpointMessage.Text = $"Save local changes before merging {comparison.Remote}/{comparison.SourceBranch} into {comparison.CurrentBranch}";
            UpdateAvailability();
        }
        else if (_mergeFlowStage == MergeFlowStage.Review && _mergeReview != null && _merge != null)
            await RunLocalMergeAsync(_mergeReview);
        return true;
    }

    private bool TryNavigateMergeFlowBack()
    {
        if (_compare == null || _loading || _closing) return false;
        if (_mergeFlowStage == MergeFlowStage.Review && _mergeReview is { } review)
        {
            ShowComparison(review);
            return true;
        }
        if (_mergeFlowStage != MergeFlowStage.Preview) return false;
        _mergeReview = null;
        _mergeFlowStage = MergeFlowStage.Source;
        _showingCachedSource = true;
        Headline.Text = "Is this the correct source branch?";
        Headline.Foreground = (Brush)FindResource("TextBrush");
        Description.Text = "";
        LoadingStatus.Text = "";
        UpdateAvailability();
        return true;
    }

    private async Task RunComparisonAsync(string source)
    {
        if (_compare == null || _loading || _closing || UnconfirmedCommand != null) return;
        _mergeReview = null;
        _mergeFlowStage = MergeFlowStage.Comparing;
        Headline.Text = "Comparing branches";
        Headline.Foreground = (Brush)FindResource("TextBrush");
        ErrorDetails.Visibility = Visibility.Collapsed;
        Height = Math.Min(760, SystemParameters.WorkArea.Height);
        using var cancellation = BeginMergeFlowOperation("Refreshing source and comparing merge conflicts…");
        try
        {
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            var review = await _compare(source, cancellation.Token, FlowProgress(cancellation));
            cancellation.Token.ThrowIfCancellationRequested();
            if (!_closing) ShowComparison(review);
        }
        catch (Exception exception)
        {
            if (exception is GitCommandExitUnconfirmedException command) UnconfirmedCommand = command;
            if (_closing) return;
            _mergeFlowStage = MergeFlowStage.Source;
            _showingCachedSource = true;
            Headline.Text = "Comparison could not finish";
            LoadingStatus.Text = UnconfirmedCommand != null
                ? "Git process exit is unconfirmed. Return to Git and use Retry to verify its exit."
                : "Review the error, then refresh and compare again.";
            ErrorDetails.Text = SensitiveDataProtection.Redact(exception.Message);
            ErrorDetails.Visibility = Visibility.Visible;
        }
        finally { EndMergeFlowOperation(); }
    }

    private void ShowComparison(GitLocalMergeReview review)
    {
        _mergeReview = review;
        _mergeFlowStage = MergeFlowStage.Preview;
        var result = review.Comparison;
        Headline.Text = result.IncomingCommits == 0
            ? "Up to date"
            : result.HasConflicts ? result.ConflictedPaths.Count > 0
                ? $"Conflicts found in {result.ConflictedPaths.Count:N0} file(s)" : "Merge conflicts found"
                : "No Git conflicts found";
        Headline.Foreground = result.HasConflicts ? new SolidColorBrush(Color.FromRgb(255, 188, 139)) : (Brush)FindResource("AccentBrush");
        LoadingStatus.Text = "";
        Description.Text = "";
        ComparisonDetails.Text = GitMergePresentation.FormatComparisonSummary(result);
        MergeOutcomeDetails.Visibility = Visibility.Collapsed;
        ResolutionPromptPanel.Visibility = Visibility.Collapsed;
        UpdateAvailability();
    }

    private async Task RunLocalMergeAsync(GitLocalMergeReview review)
    {
        if (_merge == null || _loading || _closing || UnconfirmedCommand != null) return;
        _mergeFlowStage = MergeFlowStage.Merging;
        Headline.Text = "Merging locally";
        ErrorDetails.Visibility = Visibility.Collapsed;
        using var cancellation = BeginMergeFlowOperation("Rechecking the approved source before local merge…");
        try
        {
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            // Even after cancellation, show the callback's inspected outcome. Never retry a mutation automatically.
            var outcome = await _merge(review, cancellation.Token, FlowProgress(cancellation));
            UnconfirmedCommand = outcome.UnconfirmedCommand;
            _mergeFlowStage = MergeFlowStage.Finished;
            var conflicts = outcome.ConflictedPaths;
            Headline.Text = conflicts.Count > 0 ? $"Resolve {conflicts.Count:N0} conflicted file(s)"
                : outcome.Succeeded && outcome.Result?.ReadyToFinish == true ? "Local merge prepared"
                : outcome.Succeeded ? "Local merge complete" : "Local merge needs review";
            LoadingStatus.Text = "";
            Description.Text = conflicts.Count > 0 ? "Resolve these local conflicts with Codex or Claude, then refresh Git."
                : outcome.Succeeded ? "Changes remain local. Use Commit all & push when ready to publish."
                : "Inspect the local result and recovery details before another Git action.";
            MergeOutcomeDetails.Text = outcome.Details;
            MergeOutcomeDetails.Visibility = Visibility.Visible;
            if (conflicts.Count > 0)
            {
                var pending = outcome.PendingSync;
                ResolutionPrompt.Text = GitMergePresentation.BuildResolutionPrompt(_root, conflicts, outcome.Details,
                    pending?.DestinationBranch ?? review.Comparison.CurrentBranch,
                    pending == null ? $"{review.Comparison.Remote}/{review.Comparison.SourceBranch}" : $"{pending.Remote}/{pending.SourceBranch}",
                    pending?.SourceCommit ?? review.Comparison.SourceCommit);
                ResolutionPromptPanel.Visibility = Visibility.Visible;
            }
        }
        catch (Exception exception)
        {
            if (exception is GitCommandExitUnconfirmedException command) UnconfirmedCommand = command;
            _mergeFlowStage = MergeFlowStage.Finished;
            Headline.Text = "Local merge needs review";
            LoadingStatus.Text = "Return to Git and inspect local status before another merge.";
            ErrorDetails.Text = SensitiveDataProtection.Redact(exception.Message);
            ErrorDetails.Visibility = Visibility.Visible;
        }
        finally { EndMergeFlowOperation(); }
    }

    private CancellationTokenSource BeginMergeFlowOperation(string status)
    {
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        _loading = true;
        LoadingStatus.Text = status;
        UpdateAvailability();
        return cancellation;
    }

    private IProgress<string> FlowProgress(CancellationTokenSource cancellation) => new Progress<string>(message =>
    {
        if (_loading && !_closing && ReferenceEquals(_loadCancellation, cancellation) && !cancellation.IsCancellationRequested)
            LoadingStatus.Text = SensitiveDataProtection.Redact(message);
    });

    private void EndMergeFlowOperation()
    {
        _loadCancellation = null;
        _loading = false;
        if (_closing) Close();
        else UpdateAvailability();
    }

    private void CopyResolutionPrompt_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(ResolutionPrompt.Text); CopyResolutionPrompt.Content = "Copied"; }
        catch { LoadingStatus.Text = "The clipboard is busy. Try Copy prompt again."; StatusRow.Visibility = Visibility.Visible; }
    }

    private void CopyFlowDetails_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(MergeOutcomeDetails.Visibility == Visibility.Visible ? MergeOutcomeDetails.Text
            : _mergeReview is { } review ? GitMergePresentation.FormatComparisonDetails(_root, review.Comparison) : ComparisonDetails.Text); }
        catch { LoadingStatus.Text = "The clipboard is busy. Try Copy details again."; StatusRow.Visibility = Visibility.Visible; }
    }
}
