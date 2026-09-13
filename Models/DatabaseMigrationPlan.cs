namespace FullStackLauncher.Models;

public sealed record DatabaseMigrationProject(string Name, string ProjectPath)
{
    public override string ToString() => Name;
}

/// <summary>An immutable, in-memory review of forward migrations for one exact database.</summary>
public sealed class DatabaseMigrationPlan
{
    public string ProjectPath { get; }
    public string TargetLabel { get; }
    public string ConfirmationText { get; }
    public string TargetDatabase { get; }
    public IReadOnlyList<string> RepositoryMigrations { get; }
    public IReadOnlyList<string> AppliedMigrations { get; }
    public IReadOnlyList<string> PendingMigrations { get; }
    public string Sql { get; }
    public string SqlSha256 { get; }
    public DateTimeOffset CreatedAtUtc { get; } = DateTimeOffset.UtcNow;
    public string? BlockingReason { get; }
    public bool CanApply => BlockingReason is null && PendingMigrations.Count > 0 && !string.IsNullOrWhiteSpace(Sql);
    public string Summary => BlockingReason ?? (PendingMigrations.Count == 0
        ? "All repository migrations are recorded as applied. Compare schemas separately to identify manual drift."
        : $"{PendingMigrations.Count} pending migration(s). Review the SQL before applying to the target.");

    internal string SourceId { get; }
    internal string SourceServer { get; }
    internal string DatabaseIdentity { get; }
    internal string ProjectFingerprint { get; }
    internal string SchemaFingerprint { get; }

    internal DatabaseMigrationPlan(string projectPath, DatabaseConnectionSource source, string database,
        IEnumerable<string> repository, IEnumerable<string> applied, IEnumerable<string> pending,
        string sql, string sqlSha256, string databaseIdentity, string projectFingerprint,
        string schemaFingerprint, string? blockingReason)
    {
        ProjectPath = projectPath;
        SourceId = source.Id;
        SourceServer = source.Server;
        TargetDatabase = database;
        TargetLabel = $"{source.Server} / {database}";
        ConfirmationText = TargetLabel;
        RepositoryMigrations = Array.AsReadOnly(repository.ToArray());
        AppliedMigrations = Array.AsReadOnly(applied.ToArray());
        PendingMigrations = Array.AsReadOnly(pending.ToArray());
        Sql = sql;
        SqlSha256 = sqlSha256;
        DatabaseIdentity = databaseIdentity;
        ProjectFingerprint = projectFingerprint;
        SchemaFingerprint = schemaFingerprint;
        BlockingReason = blockingReason;
    }
}

public sealed record DatabaseMigrationApplyResult(int AppliedCount, DateTimeOffset CompletedAtUtc, string Message);

/// <summary>Contains only deliberately sanitized messages suitable for an inline status.</summary>
public sealed class DatabaseMigrationException(string message) : Exception(message);
