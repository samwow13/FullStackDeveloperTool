using FullStackLauncher.Models;
using Microsoft.Data.SqlClient;

namespace FullStackLauncher.Services;

/// <summary>Routes metadata comparisons without converting definitions between database engines.</summary>
public static class DatabaseSchemaComparer
{
    private const string DifferentProviders = "Schema comparison requires two databases using the same database engine.";

    public static IReadOnlyList<string> CoverageFor(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.PostgreSql => PostgresSchemaComparer.Coverage,
        DatabaseProvider.SqlServer => SqlServerSchemaComparer.Coverage,
        _ => throw new InvalidOperationException("The database engine is not supported.")
    };

    public static Task<DatabaseSchemaSnapshot> CaptureAsync(DatabaseConnectionSource source, string database,
        CancellationToken token) => source.Provider switch
    {
        DatabaseProvider.PostgreSql => PostgresSchemaComparer.CaptureAsync(source, database, token),
        DatabaseProvider.SqlServer => SqlServerSchemaComparer.CaptureAsync(source, database, token),
        _ => throw new InvalidOperationException("The database engine is not supported.")
    };

    public static DatabaseSchemaComparison Compare(DatabaseSchemaSnapshot source, DatabaseSchemaSnapshot target)
    {
        if (source.Provider != target.Provider) throw new InvalidOperationException(DifferentProviders);
        _ = CoverageFor(source.Provider);
        // This method compares opaque logical identities and definitions; it does not execute PostgreSQL SQL.
        return PostgresSchemaComparer.Compare(source, target);
    }

    public static string Fingerprint(DatabaseSchemaSnapshot snapshot) => PostgresSchemaComparer.Fingerprint(snapshot);

    public static string DescribeError(Exception exception) => exception switch
    {
        InvalidOperationException { Message: DifferentProviders } => DifferentProviders,
        SqlException => SqlServerSchemaComparer.DescribeError(exception),
        InvalidOperationException when SqlServerSchemaComparer.IsKnownError(exception.Message) => exception.Message,
        _ => PostgresSchemaComparer.DescribeError(exception)
    };
}
