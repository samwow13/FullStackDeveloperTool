namespace FullStackLauncher.Models;

/// <summary>Small, in-memory view of a read-only local versus selected deployment comparison.</summary>
public sealed record DashboardDatabaseComparisonSummary(
    string LocalServer,
    string LocalDatabase,
    string ProductionServer,
    string ProductionDatabase,
    DateTimeOffset ComparedAtUtc,
    int LocalOnlyObjectCount,
    int ProductionOnlyObjectCount,
    int ChangedObjectCount,
    int MatchingObjectCount,
    bool MigrationHistoryAvailable,
    int LocalOnlyMigrationCount,
    int ProductionOnlyMigrationCount,
    string SchemaStatus,
    string MigrationStatus,
    IReadOnlyList<string> Warnings);
