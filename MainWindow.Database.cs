using System.Windows;
using System.Windows.Controls;
using FullStackLauncher.Models;
using FullStackLauncher.Services;
using FullStackLauncher.ViewModels;

namespace FullStackLauncher;

public partial class MainWindow
{
    internal SettingsStore DatabaseSettingsStore => _store;

    internal ProjectProfile[] DatabaseProjectsSnapshot => Projects.ToArray();

    internal ProjectProfile? ProjectForDatabaseCard(ServiceViewModel service) =>
        SelectedProject is { } project && project.Services.Any(profile => ReferenceEquals(profile, service.Profile))
            ? project : null;

    private void OpenDatabaseExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _closeRequested || IsEditing || _savingProjectEdits ||
            ServiceFrom(sender) is not { HasVerifiedDatabaseCard: true } service ||
            ProjectForDatabaseCard(service) is not { } project) return;

        var explorer = new DatabaseWorkspaceWindow(project, service, DatabaseProjectsSnapshot, _store,
            SaveExplorerSelection) { Owner = this };
        explorer.ShowDialog();
    }

    private string? SaveExplorerSelection(ProjectProfile project, DatabaseSelection selection)
    {
        if (_closing || IsEditing || !Projects.Any(current => ReferenceEquals(current, project)))
            return "This project's settings changed. Close the explorer and reopen its Database card before saving a selection.";
        if (project.Database?.SourceId == selection.SourceId && project.Database.DatabaseName == selection.DatabaseName)
            return null;
        var previous = project.Database;
        project.Database = selection;
        try
        {
            _store.Save(_settings);
            return null;
        }
        catch
        {
            project.Database = previous;
            return "The database is available for this session, but its selection could not be saved. Review settings access and reopen the explorer to try again.";
        }
    }

    private async void DatabaseCard_Expanded(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, e.OriginalSource) ||
            sender is not Expander { Content: StackPanel content }) return;
        var panel = content.Children.OfType<DatabaseComparisonPanel>().FirstOrDefault();
        if (panel is not null) await panel.RefreshSavedComparisonOnExpandAsync();
    }

    internal bool TrySaveDatabaseComparisonSet(ProjectProfile project, ServiceProfile service,
        string localSourceId, DatabaseSelection selection, out string error)
    {
        error = "";
        if (_closing || IsEditing || !ReferenceEquals(SelectedProject, project) ||
            !project.Services.Any(profile => ReferenceEquals(profile, service)))
        {
            error = "Finish project editing and select this project before saving the comparison set.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(localSourceId) || string.IsNullOrWhiteSpace(selection.SourceId) ||
            string.IsNullOrWhiteSpace(selection.DatabaseName))
        {
            error = "Choose the local and deployed databases before saving the comparison set.";
            return false;
        }

        if (service.ProductionDatabase?.SourceId == selection.SourceId &&
            service.ProductionDatabase.DatabaseName == selection.DatabaseName &&
            service.ComparisonLocalSourceId == localSourceId) return true;

        var previousTarget = service.ProductionDatabase;
        var previousLocal = service.ComparisonLocalSourceId;
        service.ProductionDatabase = new DatabaseSelection
        {
            SourceId = selection.SourceId,
            DatabaseName = selection.DatabaseName
        };
        service.ComparisonLocalSourceId = localSourceId;
        try
        {
            _store.Save(_settings);
            return true;
        }
        catch
        {
            service.ProductionDatabase = previousTarget;
            service.ComparisonLocalSourceId = previousLocal;
            error = "Comparison set could not be saved. Review settings access and try again.";
            return false;
        }
    }
}
