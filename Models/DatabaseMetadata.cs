using Npgsql;
using FullStackLauncher.Services;

namespace FullStackLauncher.Models;

public sealed class DatabaseConnectionSource
{
    private readonly string _connectionString;
    public string Id { get; }
    public string ProjectId { get; }
    public string Label { get; }
    public string Server { get; }
    public string DefaultDatabase { get; }

    public DatabaseConnectionSource(string id, string projectId, string label, string connectionString)
    {
        var securedConnection = DatabaseConnectionSecurity.NormalizePostgresConnectionString(connectionString);
        var builder = new NpgsqlConnectionStringBuilder(securedConnection);
        if (string.IsNullOrWhiteSpace(builder.Host)) throw new ArgumentException("A PostgreSQL host is required.");
        Id = id;
        ProjectId = projectId;
        Label = DatabaseConnectionSecurity.IsLoopbackOnly(builder.Host) ? label : $"{label} · TLS VerifyFull";
        Server = $"{builder.Host}:{builder.Port}";
        DefaultDatabase = string.IsNullOrEmpty(builder.Database) ? builder.Username ?? "postgres" : builder.Database;
        _connectionString = securedConnection;
    }

    internal NpgsqlConnection CreateConnection(string? database = null)
    {
        var builder = new NpgsqlConnectionStringBuilder(_connectionString)
        {
            Database = database ?? DefaultDatabase,
            // The constructor enforces verified remote TLS; bound interactive requests.
            Timeout = 15,
            CommandTimeout = 30,
            Pooling = false,
            IncludeErrorDetail = false,
            PersistSecurityInfo = false,
            LogParameters = false,
            ApplicationName = "Full Stack Launcher schema browser"
        };
        return new NpgsqlConnection(builder.ConnectionString);
    }

    public override string ToString() => Label;
}

public sealed record DatabaseDiscovery(IReadOnlyList<DatabaseConnectionSource> Sources, string Notice);
public sealed record DatabaseCatalog(string CurrentDatabase, IReadOnlyList<string> Databases, string? Warning);
public sealed record DatabaseTable(uint Id, string Schema, string Name, string Kind)
{
    public string DisplayName => $"{Schema}.{Name}";
}
public sealed record DatabaseColumn(int Position, string Name, string DataType, string Nullable,
    string DefaultExpression, string Generation, string Constraints, string Comment);

public sealed record DatabaseRowPreview(IReadOnlyList<string> Columns, IReadOnlyList<string?[]> Rows, string Ordering);

public sealed record DatabaseColumnPair(string SourceColumn, string TargetColumn);

/// <summary>A declared foreign key, directed from its referencing table to its referenced table.</summary>
public sealed record DatabaseRelationship(uint Id, string Name, DatabaseTable SourceTable, DatabaseTable TargetTable,
    IReadOnlyList<DatabaseColumnPair> ColumnPairs, string UpdateAction, string DeleteAction, string MatchType,
    bool IsOptional, bool IsUnique, bool IsValidated, bool IsDeferrable, bool IsInitiallyDeferred, string Definition)
{
    public bool IsTemporal { get; init; }
    public bool IsEnforced { get; init; } = true;
    public string Cardinality => IsTemporal ? "Temporal reference" : IsUnique ? "One to one" : "Many to one";
    public string SourceMultiplicity => !IsTemporal && IsUnique ? "0..1" : "0..many";
    public string TargetMultiplicity => IsTemporal ? "Coverage" : IsOptional ? "0..1" : "1";
    public string ColumnMapping => string.Join("\n", ColumnPairs.Select(pair => $"{pair.SourceColumn} → {pair.TargetColumn}"));
}
