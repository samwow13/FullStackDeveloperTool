using FullStackLauncher.Models;
using Microsoft.Data.SqlClient;

namespace FullStackLauncher.Services;

/// <summary>Routes explorer reads to the selected database provider; never executes user SQL.</summary>
public static class DatabaseSchemaReader
{
    public static Task<DatabaseCatalog> ReadDatabasesAsync(DatabaseConnectionSource source, CancellationToken token) =>
        source.Provider switch
        {
            DatabaseProvider.PostgreSql => PostgresSchemaReader.ReadDatabasesAsync(source, token),
            DatabaseProvider.SqlServer => SqlServerSchemaReader.ReadDatabasesAsync(source, token),
            _ => throw new NotSupportedException("This database provider is not supported.")
        };

    public static Task<IReadOnlyList<DatabaseTable>> ReadTablesAsync(DatabaseConnectionSource source, string database, CancellationToken token) =>
        source.Provider switch
        {
            DatabaseProvider.PostgreSql => PostgresSchemaReader.ReadTablesAsync(source, database, token),
            DatabaseProvider.SqlServer => SqlServerSchemaReader.ReadTablesAsync(source, database, token),
            _ => throw new NotSupportedException("This database provider is not supported.")
        };

    public static Task<IReadOnlyList<DatabaseColumn>> ReadColumnsAsync(DatabaseConnectionSource source, string database,
        DatabaseTable table, CancellationToken token) => source.Provider switch
        {
            DatabaseProvider.PostgreSql => PostgresSchemaReader.ReadColumnsAsync(source, database, table, token),
            DatabaseProvider.SqlServer => SqlServerSchemaReader.ReadColumnsAsync(source, database, table, token),
            _ => throw new NotSupportedException("This database provider is not supported.")
        };

    public static Task<DatabaseRowPreview> ReadTopRowsAsync(DatabaseConnectionSource source, string database,
        DatabaseTable table, CancellationToken token) => source.Provider switch
        {
            DatabaseProvider.PostgreSql => PostgresSchemaReader.ReadTopRowsAsync(source, database, table, token),
            DatabaseProvider.SqlServer => SqlServerSchemaReader.ReadTopRowsAsync(source, database, table, token),
            _ => throw new NotSupportedException("This database provider is not supported.")
        };

    public static Task<IReadOnlyList<DatabaseRelationship>> ReadRelationshipsAsync(DatabaseConnectionSource source,
        string database, DatabaseTable table, CancellationToken token) => source.Provider switch
        {
            DatabaseProvider.PostgreSql => PostgresSchemaReader.ReadRelationshipsAsync(source, database, table, token),
            DatabaseProvider.SqlServer => SqlServerSchemaReader.ReadRelationshipsAsync(source, database, table, token),
            _ => throw new NotSupportedException("This database provider is not supported.")
        };

    public static string DescribeError(Exception exception) => exception is SqlException
        ? SqlServerSchemaReader.DescribeError(exception)
        : PostgresSchemaReader.DescribeError(exception);
}
