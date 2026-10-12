namespace FullStackLauncher.Services;

/// <summary>Preserves each dialog's normal close guards during dashboard replacement.</summary>
internal interface IDashboardReplacementWindow
{
    bool IsDashboardReplacementReady { get; }
    bool IsDashboardReplacementClosePending { get; }
}
