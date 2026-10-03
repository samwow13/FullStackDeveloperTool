using System.Data.Common;
using Microsoft.Data.SqlClient;
using Npgsql;
using FullStackLauncher.Services;

namespace FullStackLauncher.Models;

public enum DatabaseProvider { PostgreSql, SqlServer }

public sealed class DatabaseConnectionSource
{
    private readonly string _connectionString;
    public string Id { get; }
    public string ProjectId { get; }
    public string Label { get; }
    public string Server { get; }
    public string DefaultDatabase { get; }
    public DatabaseProvider Provider { get; }
    public string ProviderLabel => Provider == DatabaseProvider.SqlServer ? "SQL Server" : "PostgreSQL";
    public bool IsLoopback { get; }
    public bool IsRemoteOnly { get; }
    public bool IsSingleHost { get; }

    public DatabaseConnectionSource(string id, string projectId, string label, string connectionString,
        DatabaseProvider? provider = null)
    {
        Provider = provider ?? DatabaseConnectionSecurity.DetectProvider(connectionString)
            ?? throw new ArgumentException("Choose PostgreSQL or SQL Server for this connection. Its database provider could not be determined safely.");
        var securedConnection = DatabaseConnectionSecurity.NormalizeConnectionString(connectionString, Provider);
        Id = id;
        ProjectId = projectId;
        if (Provider == DatabaseProvider.PostgreSql)
        {
            var builder = new NpgsqlConnectionStringBuilder(securedConnection);
            var hosts = builder.Host!.Split(',');
            IsSingleHost = hosts.Length == 1;
            IsLoopback = DatabaseConnectionSecurity.IsLoopbackOnly(builder.Host);
            IsRemoteOnly = hosts.All(host => !string.IsNullOrWhiteSpace(host) &&
                !DatabaseConnectionSecurity.IsLoopbackOnly(host));
            Label = $"{label} · {ProviderLabel}" + (IsLoopback ? "" : " · TLS VerifyFull");
            Server = $"{builder.Host}:{builder.Port}";
            DefaultDatabase = string.IsNullOrEmpty(builder.Database) ? builder.Username ?? "postgres" : builder.Database;
        }
        else
        {
            var builder = new SqlConnectionStringBuilder(securedConnection);
            IsSingleHost = true;
            IsLoopback = DatabaseConnectionSecurity.IsSqlServerLoopback(builder.DataSource);
            IsRemoteOnly = !IsLoopback;
            Label = $"{label} · {ProviderLabel}" + (IsLoopback ? "" : " · Verified TLS");
            Server = builder.DataSource;
            DefaultDatabase = builder.InitialCatalog;
        }
        _connectionString = securedConnection;
    }

    internal NpgsqlConnection CreateConnection(string? database = null)
    {
        if (Provider != DatabaseProvider.PostgreSql)
            throw new InvalidOperationException("This operation supports PostgreSQL connections only.");
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

    internal DbConnection CreateDbConnection(string? database = null) => Provider == DatabaseProvider.SqlServer
        ? CreateSqlServerConnection(database) : CreateConnection(database);

    internal SqlConnection CreateSqlServerConnection(string? database = null)
    {
        if (Provider != DatabaseProvider.SqlServer)
            throw new InvalidOperationException("This operation supports SQL Server connections only.");
        var builder = new SqlConnectionStringBuilder(_connectionString)
        {
            InitialCatalog = database ?? DefaultDatabase,
            ConnectTimeout = 15,
            CommandTimeout = 30,
            ConnectRetryCount = 0,
            Pooling = false,
            PersistSecurityInfo = false,
            Enlist = false,
            ApplicationName = "Full Stack Launcher schema browser"
        };
        return new SqlConnection(builder.ConnectionString);
    }

    /// <summary>Compares the full normalized connection, including credentials, without exposing it.</summary>
    internal bool MatchesNormalizedConnection(string normalizedConnection)
    {
        try
        {
            var applied = DatabaseConnectionSecurity.NormalizeConnectionString(normalizedConnection, Provider);
            return Provider == DatabaseProvider.SqlServer
                ? new SqlConnectionStringBuilder(_connectionString).EquivalentTo(new SqlConnectionStringBuilder(applied))
                : new NpgsqlConnectionStringBuilder(_connectionString).EquivalentTo(new NpgsqlConnectionStringBuilder(applied));
        }
        catch (ArgumentException) { return false; }
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
