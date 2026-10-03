using System.Windows;
using System.Windows.Threading;
using FullStackLauncher.CodexMonitor;

namespace FullStackLauncher;

public partial class MainWindow
{
    private readonly DispatcherTimer _queueActivityTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private QueueActivitySnapshot _queueActivity = QueueActivitySnapshot.Empty;
    private bool _queueActivityRefreshing;

    public string QueueActivitySummary => "Queue activity · " + _queueActivity.StatusFor(SelectedProject?.Id);

    private ChatProgressRow? QueueLatestCompletion => _queueActivity.Rows
        .Where(row => row.IsCompleted && row.State == AgentRunState.Completed &&
            row.ProjectId == SelectedProject?.Id)
        .OrderByDescending(row => row.CompletedAt).FirstOrDefault();

    public bool HasQueueRecent => QueueLatestCompletion != null;
    public string QueueRecentSummary => QueueLatestCompletion is { } row
        ? $"{row.ActivityTitle} · {row.CompletedAt?.ToLocalTime():g}"
        : "";

    private void InitializeQueueActivity()
    {
        _queueActivityTimer.Tick += async (_, _) => await RefreshQueueActivityAsync();
        Loaded += async (_, _) =>
        {
            _queueActivityTimer.Start();
            await RefreshQueueActivityAsync();
        };
        Closed += (_, _) => _queueActivityTimer.Stop();
    }

    private async Task RefreshQueueActivityAsync()
    {
        if (_queueActivityRefreshing || _closed) return;
        _queueActivityRefreshing = true;
        try
        {
            var snapshot = await Task.Run(() => QueueActivityProjection.Read());
            if (_closed) return;
            _queueActivity = snapshot;
            NotifyQueueActivityChanged();
        }
        finally { _queueActivityRefreshing = false; }
    }

    private void NotifyQueueActivityChanged()
    {
        Changed(nameof(QueueActivitySummary));
        Changed(nameof(QueueRecentSummary));
        Changed(nameof(HasQueueRecent));
    }

    private static void AddQueueStoreSettingsArgument(List<string> arguments)
    {
        var taskStorePath = new ProjectTasks.ProjectTaskStore().StorePath;
        if (taskStorePath.Equals(ProjectTasks.ProjectTaskStore.DefaultStorePath, StringComparison.OrdinalIgnoreCase)) return;
        const string suffix = ".project-tasks.json";
        arguments.Add("--settings");
        arguments.Add(taskStorePath[..^suffix.Length]);
    }
}
