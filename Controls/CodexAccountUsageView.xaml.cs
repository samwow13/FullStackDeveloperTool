using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using FullStackLauncher.Models;

namespace FullStackLauncher.Controls;

public partial class CodexAccountUsageView : UserControl, INotifyPropertyChanged
{
    public static readonly DependencyProperty SnapshotProperty = DependencyProperty.Register(
        nameof(Snapshot), typeof(CodexAccountUsageSnapshot), typeof(CodexAccountUsageView),
        new PropertyMetadata(null, SnapshotChanged));

    public CodexAccountUsageSnapshot? Snapshot
    {
        get => (CodexAccountUsageSnapshot?)GetValue(SnapshotProperty);
        set => SetValue(SnapshotProperty, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public CodexAccountUsageView() => InitializeComponent();

    public string Summary
    {
        get
        {
            var values = new List<string>();
            if (Snapshot?.Primary is { } primary) values.Add(WindowSummary(primary, "Primary"));
            if (Snapshot?.Secondary is { } secondary) values.Add(WindowSummary(secondary, "Secondary"));
            if (values.Count == 0) values.Add("Allowance —");
            values.Add(Snapshot?.UnlimitedCredits == true ? "Credits unlimited"
                : Snapshot?.CreditsBalance is { } balance ? $"Credits {CreditSummary(balance)}"
                : "Credits —");
            return string.Join(" · ", values) + (Snapshot?.IsStale == true && Snapshot.HasData ? " (last read)" : "");
        }
    }

    public string Detail
    {
        get
        {
            var lines = new List<string> { "Codex account allowance remaining. Shared across this signed-in account." };
            if (Snapshot is null) lines.Add("Reading account allowance and credits…");
            else
            {
                AddWindowDetail(lines, Snapshot.Primary, "Primary");
                AddWindowDetail(lines, Snapshot.Secondary, "Secondary");
                if (Snapshot.UnlimitedCredits == true) lines.Add("Credits: unlimited.");
                else if (Snapshot.CreditsBalance is { } balance) lines.Add($"Credits remaining: {balance.ToString(CultureInfo.CurrentCulture)}.");
                else lines.Add("Credit balance was not returned by Codex.");
                if (Snapshot.LastUpdatedAt is { } updated) lines.Add($"Last successful read: {updated.ToLocalTime():g}.");
                if (Snapshot.IsStale && Snapshot.HasData) lines.Add("Values are from the last successful read; the latest read failed.");
                if (!string.IsNullOrWhiteSpace(Snapshot.StatusMessage)) lines.Add(Snapshot.StatusMessage);
            }
            return string.Join(Environment.NewLine, lines);
        }
    }

    private static string WindowSummary(CodexAccountUsageWindow window, string fallback) =>
        $"{WindowName(window.WindowDurationMins, fallback)} {(window.RemainingPercent is > 0 and < 0.01 ? "<0.01" : window.RemainingPercent.ToString("0.##", CultureInfo.CurrentCulture))}% left";

    private static string CreditSummary(decimal balance) => balance switch
    {
        > 0 and < 0.01m => "<0.01",
        0 => "0",
        _ => balance.ToString("N2", CultureInfo.CurrentCulture)
    };

    private static string WindowName(int? minutes, string fallback) => minutes switch
    {
        10080 => "Weekly",
        1440 => "Daily",
        > 0 when minutes % 60 == 0 => $"{minutes / 60}h",
        > 0 => $"{minutes}m",
        _ => fallback
    };

    private static void AddWindowDetail(List<string> lines, CodexAccountUsageWindow? window, string fallback)
    {
        if (window is null) return;
        lines.Add(WindowSummary(window, fallback) + (window.ResetsAt is { } reset ? $"; resets {reset.ToLocalTime():g}." : "."));
    }

    private static void SnapshotChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var view = (CodexAccountUsageView)sender;
        view.PropertyChanged?.Invoke(view, new(nameof(Summary)));
        view.PropertyChanged?.Invoke(view, new(nameof(Detail)));
    }
}
