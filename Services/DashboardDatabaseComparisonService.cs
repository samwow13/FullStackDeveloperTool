using FullStackLauncher.Models;
using System.Security.Cryptography;
using System.Text;

namespace FullStackLauncher.Services;

/// <summary>
/// Explicit dashboard comparison. It never probes production until CompareAsync is called.
/// Endpoint selection holds only source IDs and database names; credentials stay in the source store.
/// </summary>
public static class DashboardDatabaseComparisonService
{
    /// <summary>Store endpoint identity beside the stable source ID without persisting credentials.</summary>
    public static DatabaseSelection CreateProductionSelection(DatabaseConnectionSource source, string database)
    {
        if (source is null || string.IsNullOrWhiteSpace(database))
            throw new ArgumentException("Choose a deployed connection and database name.");
        return new DatabaseSelection { SourceId = ProductionReference(source), DatabaseName = database.Trim() };
    }

    public static bool MatchesProductionSelection(DatabaseSelection? selection, DatabaseConnectionSource? source) =>
        selection is not null && source is not null && !string.IsNullOrWhiteSpace(selection.DatabaseName) &&
        string.Equals(selection.SourceId, ProductionReference(source), StringComparison.Ordinal);

    public static IReadOnlyList<DatabaseConnectionSource> GetLocalCandidates(
        IEnumerable<DatabaseConnectionSource> sources, string projectId, string apiServiceId,
        string verifiedApiDatabaseName)
    {
        if (!IsVerifiedName(verifiedApiDatabaseName) || string.IsNullOrWhiteSpace(projectId) ||
            string.IsNullOrWhiteSpace(apiServiceId)) return [];

        return sources.Where(source => IsLocalSource(source, projectId, apiServiceId, verifiedApiDatabaseName))
            .GroupBy(source => source.Id, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .Select(group => group.Single())
            .OrderBy(source => source.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<DatabaseConnectionSource> GetProductionCandidates(
        IEnumerable<DatabaseConnectionSource> sources, string projectId) =>
        sources.Where(source => IsProductionSource(source, projectId))
            .GroupBy(source => source.Id, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .Select(group => group.Single())
            .OrderBy(source => source.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static async Task<DashboardDatabaseComparisonSummary> CompareAsync(
        string projectId, string apiServiceId, string verifiedApiDatabaseName,
        DatabaseConnectionSource localSource, DatabaseSelection productionSelection,
        IEnumerable<DatabaseConnectionSource> sources, CancellationToken token)
    {
        if (!IsVerifiedName(verifiedApiDatabaseName))
            throw new InvalidOperationException("The running local API has not verified its database name. Refresh its health before comparing.");
        if (localSource is null || productionSelection is null ||
            string.IsNullOrWhiteSpace(productionSelection.SourceId) ||
            string.IsNullOrWhiteSpace(productionSelection.DatabaseName))
            throw new InvalidOperationException("Choose the local connection and deployed database before comparing.");

        // Resolve both selections against the caller's current discovery. Do not trust an old source object or a
        // stale saved target after credentials, configuration, or the project have changed.
        var available = sources.ToArray();
        var locals = available.Where(source => source.Id == localSource.Id).ToArray();
        if (locals.Length != 1 ||
            !IsLocalSource(locals[0], projectId, apiServiceId, verifiedApiDatabaseName))
            throw new InvalidOperationException("The local API connection is unavailable or ambiguous. Refresh connections and choose its verified Local source.");
        var local = locals[0];

        var targets = available.Where(source => MatchesProductionSelection(productionSelection, source)).ToArray();
        if (targets.Length != 1 || !IsProductionSource(targets[0], projectId))
            throw new InvalidOperationException("The selected deployed connection is unavailable or ambiguous. Choose a remote saved or Prod source again.");
        var production = targets[0];
        if (local.Provider != production.Provider)
            throw new InvalidOperationException("Choose a deployed database using the same engine as the local database. Cross-provider schema comparison is not supported.");
        if (string.Equals(local.Server, production.Server, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(verifiedApiDatabaseName, productionSelection.DatabaseName, StringComparison.Ordinal))
            throw new InvalidOperationException("Local and deployed selections point to the same server and database. Choose a separate deployed database.");

        DatabaseSchemaSnapshot[] captures;
        try
        {
            var localTask = DatabaseSchemaComparer.CaptureAsync(local, verifiedApiDatabaseName, token);
            var productionTask = DatabaseSchemaComparer.CaptureAsync(production, productionSelection.DatabaseName, token);
            captures = await Task.WhenAll(localTask, productionTask);
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Database exception messages can contain connection details or server-supplied text.
            throw new InvalidOperationException(DatabaseSchemaComparer.DescribeError(exception));
        }

        if (!string.Equals(captures[0].Database, verifiedApiDatabaseName, StringComparison.Ordinal) ||
            !string.Equals(captures[1].Database, productionSelection.DatabaseName, StringComparison.Ordinal))
            throw new InvalidOperationException("The connected database identity changed. Refresh connections and compare again.");

        var comparison = DatabaseSchemaComparer.Compare(captures[0], captures[1]);
        var localOnly = comparison.Differences.Count(item => item.Change == DatabaseSchemaChange.SourceOnly);
        var productionOnly = comparison.Differences.Count(item => item.Change == DatabaseSchemaChange.TargetOnly);
        var changed = comparison.Differences.Count(item => item.Change == DatabaseSchemaChange.Changed);
        var total = localOnly + productionOnly + changed;
        var localHistoryTable = comparison.Source.MigrationHistory.TableName is not null;
        var productionHistoryTable = comparison.Target.MigrationHistory.TableName is not null;
        var completeHistory = comparison.CanCompareMigrationHistory && localHistoryTable && productionHistoryTable;
        var migrationStatus = !comparison.CanCompareMigrationHistory
            ? "EF migration history is unavailable or ambiguous on at least one database."
            : !localHistoryTable && !productionHistoryTable
                ? "Neither database has a standard EF migration history table. Recorded migration progress is unknown."
            : !localHistoryTable || !productionHistoryTable
                ? $"A standard EF migration history table is missing on {(!localHistoryTable ? "local" : "deployed")}. " +
                  $"{comparison.MigrationIdsOnlyInSource.Count} IDs appear only in local; " +
                  $"{comparison.MigrationIdsOnlyInTarget.Count} only in deployed. A custom history table is not detected."
            : comparison.MigrationIdsOnlyInSource.Count == 0 && comparison.MigrationIdsOnlyInTarget.Count == 0
                ? "Recorded EF migration IDs match."
                : $"{comparison.MigrationIdsOnlyInSource.Count} EF migration IDs only in local; " +
                  $"{comparison.MigrationIdsOnlyInTarget.Count} only in deployed.";
        var warnings = comparison.Warnings.Concat(new[]
        {
            $"Counts cover captured {local.ProviderLabel} schema metadata, not row data or deployment effort.",
            "Owners, grants, comments, and other environment-specific metadata can add differences without an application migration."
        }).Distinct(StringComparer.Ordinal).ToArray();

        return new(
            captures[0].Server, captures[0].Database, captures[1].Server, captures[1].Database,
            DateTimeOffset.UtcNow, localOnly, productionOnly, changed, comparison.UnchangedCount,
            completeHistory, comparison.MigrationIdsOnlyInSource.Count,
            comparison.MigrationIdsOnlyInTarget.Count,
            total == 0 ? "No covered schema differences found." : $"{total} covered schema differences found.",
            migrationStatus, warnings);
    }

    private static bool IsVerifiedName(string name) =>
        name is not null && string.Equals(name, ApiDatabaseIdentifier.SafeName(name), StringComparison.Ordinal);

    private static bool IsLocalSource(DatabaseConnectionSource source, string projectId,
        string apiServiceId, string verifiedDatabaseName) =>
        IsVerifiedName(verifiedDatabaseName) && !string.IsNullOrWhiteSpace(projectId) && !string.IsNullOrWhiteSpace(apiServiceId) &&
        source.IsLoopback && source.IsSingleHost &&
        string.Equals(source.ProjectId, projectId, StringComparison.Ordinal) &&
        source.Id.StartsWith(apiServiceId + "/", StringComparison.Ordinal) &&
        source.Id.Contains("/local/", StringComparison.Ordinal) &&
        (string.Equals(source.DefaultDatabase, verifiedDatabaseName, StringComparison.Ordinal) ||
         // SQL Server may use the login's default database. The caller must still match this exact
         // source against the running API's captured provider and connection; capture explicitly
         // targets the database name verified by that API, not a newly resolved login default.
         source.Provider == DatabaseProvider.SqlServer && string.IsNullOrEmpty(source.DefaultDatabase));

    private static bool IsProductionSource(DatabaseConnectionSource source, string projectId) =>
        source.IsRemoteOnly &&
        (source.Id.StartsWith("saved/", StringComparison.Ordinal) ||
         (string.Equals(source.ProjectId, projectId, StringComparison.Ordinal) &&
          source.Id.Contains("/prod/", StringComparison.Ordinal)));

    private static string ProductionReference(DatabaseConnectionSource source)
    {
        // Preserve existing PostgreSQL references; SQL Server references include the engine identity.
        var server = (source.Provider == DatabaseProvider.PostgreSql ? "" : source.Provider + "|") + source.Server.ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(server)));
        return source.Id + "#endpoint:" + hash;
    }
}
