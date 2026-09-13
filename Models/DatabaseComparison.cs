namespace FullStackLauncher.Models;

public enum DatabaseSchemaChange { SourceOnly, TargetOnly, Changed }

/// <summary>Logical identity deliberately excludes database-local OIDs.</summary>
public sealed record DatabaseSchemaObject(string Kind, string Schema, string Name, string Definition)
{
    public string Key => $"{Kind}\0{Schema}\0{Name}";
    public string DisplayName => string.IsNullOrEmpty(Schema) ? Name : $"{Schema}.{Name}";
}

public sealed record DatabaseMigrationHistory(bool IsAvailable, string? TableName,
    IReadOnlyList<string> MigrationIds, string? Warning);

public sealed record DatabaseSchemaSnapshot(string SourceId, string Server, string Database, string ServerVersion,
    DateTimeOffset CapturedAtUtc, IReadOnlyList<DatabaseSchemaObject> Objects,
    DatabaseMigrationHistory MigrationHistory, IReadOnlyList<string> Warnings, IReadOnlyList<string> Coverage);

public sealed record DatabaseSchemaDifference(string Kind, string Schema, string Name, DatabaseSchemaChange Change,
    string? SourceDefinition, string? TargetDefinition)
{
    public string DisplayName => string.IsNullOrEmpty(Schema) ? Name : $"{Schema}.{Name}";
    public string ChangeLabel => Change switch
    {
        DatabaseSchemaChange.SourceOnly => "Only in source",
        DatabaseSchemaChange.TargetOnly => "Only in target",
        _ => "Changed"
    };
}

public sealed record DatabaseSchemaComparison(DatabaseSchemaSnapshot Source, DatabaseSchemaSnapshot Target,
    IReadOnlyList<DatabaseSchemaDifference> Differences, int UnchangedCount,
    IReadOnlyList<string> MigrationIdsOnlyInSource, IReadOnlyList<string> MigrationIdsOnlyInTarget)
{
    public bool CanCompareMigrationHistory => Source.MigrationHistory.IsAvailable && Target.MigrationHistory.IsAvailable;
    public IReadOnlyList<string> Warnings => Source.Warnings.Concat(Target.Warnings)
        .Concat(string.Equals(Source.ServerVersion, Target.ServerVersion, StringComparison.Ordinal)
            ? [] : new[] { "The PostgreSQL versions differ. Some reported changes may reflect version-specific definitions or defaults." })
        .Distinct(StringComparer.Ordinal).ToArray();
}
