using System.Text.Json;
using System.Windows;
using FullStackLauncher.Models;

namespace FullStackLauncher;

public partial class MainWindow
{
    public bool CanChangeBatchSelection => !IsEditing && !_addingProjectService && !_savingProjectEdits &&
        !_checkingStartupServices && !_closeRequested && !_batchBusy && !_forceStopBatchBusy && !_closing;

    private void BatchServiceSelection_Click(object sender, RoutedEventArgs e)
    {
        if (ServiceFrom(sender) is not { } service) return;
        if (!CanChangeBatchSelection || SelectedProject is not { } project || !Services.Contains(service))
        {
            service.RefreshBatchSelection();
            return;
        }

        var included = !service.IncludeInBatch;
        try
        {
            var candidate = JsonSerializer.Deserialize<LauncherSettings>(JsonSerializer.Serialize(_settings))!;
            candidate.Projects.Single(item => item.Id == project.Id).Services
                .Single(item => item.Id == service.Profile.Id).IncludeInBatch = included;
            _store.Save(candidate);
            service.Profile.IncludeInBatch = included;
            Notice = $"{service.Name}: {(included ? "included in" : "excluded from")} Start all, Restart all and Force stop all.";
        }
        catch
        {
            Notice = "Batch selection could not be saved. The previous saved choice is kept.";
        }
        finally
        {
            service.RefreshBatchSelection();
            UpdateActions();
        }
    }
}
