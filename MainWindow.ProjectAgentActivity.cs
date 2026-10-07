using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FullStackLauncher.Models;
using FullStackLauncher.Services;
using FullStackLauncher.ViewModels;

namespace FullStackLauncher;

public partial class MainWindow
{
    private readonly ProjectAgentActivityTracker _projectAgentActivity = new();

    private void InitializeProjectAgentActivity()
    {
        CodexCrewPanel.AllProjectsSnapshotChanged += ApplyProjectAgentActivity;
        ProjectList.PreviewMouseLeftButtonDown += ProjectList_AcknowledgeAgentCompletion;
        Closed += (_, _) =>
        {
            CodexCrewPanel.AllProjectsSnapshotChanged -= ApplyProjectAgentActivity;
            ProjectList.PreviewMouseLeftButtonDown -= ProjectList_AcknowledgeAgentCompletion;
        };
    }

    private void ApplyProjectAgentActivity(CodexActivityFeedSnapshot snapshot)
    {
        if (_closed) return;
        _projectAgentActivity.Update(snapshot, SelectedProject?.Id);
        RefreshProjectAgentActivity();
    }

    private void RefreshProjectAgentActivity()
    {
        foreach (var project in ProjectItems)
        {
            var activity = _projectAgentActivity.GetActivity(project.Profile.Id);
            project.UpdateAgentActivity(activity with
            {
                HasUnreadCompletion = activity.HasUnreadCompletion && project.Profile.Id != SelectedProject?.Id
            });
        }
    }

    private void AcknowledgeProjectAgentCompletion(string projectId)
    {
        _projectAgentActivity.Acknowledge(projectId);
        RefreshProjectAgentActivity();
    }

    private void ProjectList_AcknowledgeAgentCompletion(object sender, MouseButtonEventArgs e)
    {
        if (!CanChangeProject || _refreshingProjectList || e.OriginalSource is not DependencyObject source) return;
        var container = ItemsControl.ContainerFromElement(ProjectList, source) as ListBoxItem;
        if (container?.DataContext is not ProjectViewModel project) return;
        // Expanding details and interacting with their controls is separate from
        // opening a project. Do not consume completion through those controls.
        for (var current = source; current is not null && current != container;
             current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is ButtonBase or ScrollBar) return;
        AcknowledgeProjectAgentCompletion(project.Profile.Id);
    }
}
