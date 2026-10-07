namespace FullStackLauncher.Models;

/// <summary>A backend-reported Codex allowance window; percentages never imply permission to run work.</summary>
public sealed record CodexAccountUsageWindow(
    double RemainingPercent,
    int? WindowDurationMins,
    DateTimeOffset? ResetsAt);

/// <summary>Read-only account usage. Missing values stay unknown, and failed reads retain explicitly stale data.</summary>
public sealed record CodexAccountUsageSnapshot
{
    public CodexAccountUsageWindow? Primary { get; init; }
    public CodexAccountUsageWindow? Secondary { get; init; }
    public decimal? CreditsBalance { get; init; }
    public bool? UnlimitedCredits { get; init; }
    public bool? HasCredits { get; init; }
    public DateTimeOffset? LastUpdatedAt { get; init; }
    public DateTimeOffset LastAttemptedAt { get; init; }
    public bool IsStale { get; init; }
    public string? StatusMessage { get; init; }

    public bool HasData => Primary is not null || Secondary is not null
        || CreditsBalance is not null || UnlimitedCredits is not null || HasCredits is not null;
}
