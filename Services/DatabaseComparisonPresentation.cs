using FullStackLauncher.Models;

namespace FullStackLauncher.Services;

public sealed record DatabaseComparisonPresentationText(
    string Header,
    string SchemaSummary,
    string MigrationSummary);

/// <summary>Plain-language summary of a completed, read-only schema comparison.</summary>
public static class DatabaseComparisonPresentation
{
    public static DatabaseComparisonPresentationText Format(DashboardDatabaseComparisonSummary comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);

        var localOnly = comparison.LocalOnlyObjectCount;
        var deployedOnly = comparison.ProductionOnlyObjectCount;
        var changed = comparison.ChangedObjectCount;
        var schemasDiffer = localOnly != 0 || deployedOnly != 0 || changed != 0;

        var header = GetHeader(comparison, schemasDiffer);
        var schemaSummary = GetSchemaSummary(comparison, schemasDiffer);
        var migrationSummary = GetMigrationSummary(comparison, schemasDiffer);
        return new(header, schemaSummary, migrationSummary);
    }

    private static string GetHeader(DashboardDatabaseComparisonSummary comparison, bool schemasDiffer)
    {
        if (!schemasDiffer) return "Local and deployed match on checked schema details.";

        var parts = new List<string>(3);
        if (comparison.LocalOnlyObjectCount > 0)
            parts.Add($"Local has {Count(comparison.LocalOnlyObjectCount)} {Plural(comparison.LocalOnlyObjectCount, "object", "objects")} deployed lacks");
        if (comparison.ProductionOnlyObjectCount > 0)
            parts.Add($"Local lacks {Count(comparison.ProductionOnlyObjectCount)} deployed {Plural(comparison.ProductionOnlyObjectCount, "object", "objects")}");
        if (comparison.ChangedObjectCount > 0)
            parts.Add($"{Count(comparison.ChangedObjectCount)} shared {Plural(comparison.ChangedObjectCount, "definition differs", "definitions differ")}");

        var header = string.Join("; ", parts) + ".";
        return comparison.ChangedObjectCount > 0 ||
               comparison.LocalOnlyObjectCount > 0 && comparison.ProductionOnlyObjectCount > 0
            ? header + " Cannot tell which schema is ahead."
            : header;
    }

    private static string GetSchemaSummary(DashboardDatabaseComparisonSummary comparison, bool schemasDiffer)
    {
        if (!schemasDiffer)
            return $"No differences found in the checked schema metadata. {Count(comparison.MatchingObjectCount)} {Plural(comparison.MatchingObjectCount, "object", "objects")} match.";

        var parts = new List<string>(4);
        if (comparison.LocalOnlyObjectCount > 0)
            parts.Add($"Local has {Count(comparison.LocalOnlyObjectCount)} schema {Plural(comparison.LocalOnlyObjectCount, "object", "objects")} deployed lacks.");
        if (comparison.ProductionOnlyObjectCount > 0)
            parts.Add($"Deployed has {Count(comparison.ProductionOnlyObjectCount)} schema {Plural(comparison.ProductionOnlyObjectCount, "object", "objects")} local lacks.");
        if (comparison.ChangedObjectCount > 0)
            parts.Add($"{Count(comparison.ChangedObjectCount)} shared {Plural(comparison.ChangedObjectCount, "object has", "objects have")} different definitions.");
        parts.Add($"{Count(comparison.MatchingObjectCount)} {Plural(comparison.MatchingObjectCount, "object", "objects")} match.");
        return string.Join(" ", parts);
    }

    private static string GetMigrationSummary(DashboardDatabaseComparisonSummary comparison, bool schemasDiffer)
    {
        if (!comparison.MigrationHistoryAvailable) return comparison.MigrationStatus;

        var localOnly = comparison.LocalOnlyMigrationCount;
        var deployedOnly = comparison.ProductionOnlyMigrationCount;
        if (localOnly == 0 && deployedOnly == 0)
            return schemasDiffer
                ? "Recorded EF migration IDs match, but the schemas differ."
                : "Recorded EF migration IDs match.";
        if (localOnly > 0 && deployedOnly == 0)
            return $"Local has {Count(localOnly)} recorded EF {Plural(localOnly, "migration", "migrations")} deployed lacks. This does not establish a schema lead.";
        if (deployedOnly > 0 && localOnly == 0)
            return $"Deployed has {Count(deployedOnly)} recorded EF {Plural(deployedOnly, "migration", "migrations")} local lacks. This does not establish a schema lead.";
        return $"Migration history differs on both sides: {Count(localOnly)} IDs only in local and {Count(deployedOnly)} only in deployed. Neither migration history is clearly ahead.";
    }

    private static string Count(int value) => value.ToString("N0");

    private static string Plural(int value, string singular, string plural) => value == 1 ? singular : plural;
}
