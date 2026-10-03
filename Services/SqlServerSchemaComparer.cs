using System.Globalization;
using FullStackLauncher.Models;
using Microsoft.Data.SqlClient;

namespace FullStackLauncher.Services;

/// <summary>Reads bounded SQL Server catalog metadata. Never executes synchronization SQL.</summary>
public static class SqlServerSchemaComparer
{
    private const int MaximumObjects = 100_000;
    private const int MaximumDefinitionLength = 1_048_576;
    private const long MaximumSnapshotCharacters = 32_000_000;
    private const int MaximumMigrations = 20_000;
    private const string VersionError = "Schema comparison requires SQL Server 2016 or later, or Azure SQL Database.";
    private const string PermissionError = "SQL Server schema comparison requires database VIEW DEFINITION permission without metadata-denying permissions. No partial comparison was produced.";
    private const string DefinitionError = "SQL Server contains encrypted or inaccessible definitions. No partial comparison was produced.";
    private const string SizeError = "The schema exceeds the comparison size limit. No partial comparison was produced.";
    private const string IdentityError = "The database contains ambiguous object identities. No partial comparison was produced.";
    private const string ConcurrentWarning = "SQL Server catalogs are read in separate statements. Concurrent schema changes can affect a capture; compare while schema changes are paused. The databases are not captured at one shared point in time.";
    private const string VisibilityWarning = "SQL Server metadata visibility follows the connected account's permissions. Database VIEW DEFINITION is required and detected metadata denials fail the capture; no differences refers only to captured metadata within the stated coverage.";

    public static IReadOnlyList<string> Coverage { get; } = Array.AsReadOnly(new[]
    {
        "Compares SQL Server user schemas and owners; database collation and compatibility level; tables and basic temporal/memory-optimized properties; columns, SQL types, nullability, collation, defaults, identity seeds/increments, computed expressions and basic encryption metadata; primary/unique keys, foreign keys and check constraints; index options and ordered/included columns; T-SQL views, procedures, functions and DML triggers; sequence definitions; synonyms; alias types and table-type columns, keys and indexes.",
        "Reads catalog metadata and standard EF migration IDs only. Does not execute user routines, write database rows, generate synchronization SQL or apply migrations. Table contents, identity/sequence current values, row counts and statistics are not compared.",
        "Outside coverage: server logins, linked servers, SQL Agent jobs, database users/roles/permissions, effective access, extended properties, partition functions/schemes/boundaries, filegroups/storage/compression, XML schema collections, XML/spatial/full-text/vector/hash/columnstore-specific index internals, CLR implementations, external tables/data sources, graph/ledger details, row-security policies, dynamic data masking, encryption key definitions, DDL/server triggers and trigger execution order, Service Broker, replication/CDC, database/server configuration beyond the listed defaults, and numbered procedure overloads. Basic index kind/columns are captured, not every specialized option.",
        "Requires database VIEW DEFINITION and readable covered definitions. Encrypted or detected inaccessible SQL modules, expressions or filters prevent a comparison. Metadata visibility still depends on SQL Server permissions; this is not a complete database deployment verification.",
        ConcurrentWarning,
        "Definitions retain SQL Server formatting and generated constraint names; these can differ despite similar behavior. Owners may intentionally differ by environment. No differences means the captured metadata agrees within this coverage, not that application data or historical migrations are equivalent.",
        "EF discovery supports __EFMigrationsHistory in any non-system schema. Renamed tables are outside discovery. Multiple matches, missing permissions, row-security policies, masked history columns, unexpected column types or invalid IDs make migration history unavailable. Applied IDs do not verify historical migration file contents."
    });

    public static async Task<DatabaseSchemaSnapshot> CaptureAsync(DatabaseConnectionSource source, string database,
        CancellationToken token)
    {
        if (source.Provider != DatabaseProvider.SqlServer)
            throw new InvalidOperationException("The database engine is not supported.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var requestToken = timeout.Token;
        await using var connection = source.CreateSqlServerConnection(database);
        await connection.OpenAsync(requestToken);
        await CheckMetadataAccessAsync(connection, requestToken);
        // Session settings are bounded and affect only this connection. Every data statement is SELECT.
        await using (var setup = new SqlCommand("SET LOCK_TIMEOUT 3000; SET DEADLOCK_PRIORITY LOW;", connection))
        {
            setup.CommandTimeout = 10;
            await setup.ExecuteNonQueryAsync(requestToken);
        }
        var objects = await ReadObjectsAsync(connection, requestToken);
        var history = await ReadMigrationHistoryAsync(connection, requestToken);
        var warnings = new List<string> { ConcurrentWarning, VisibilityWarning };
        if (history.Warning is { Length: > 0 } warning) warnings.Add(warning);
        return new(source.Id, source.Server, connection.Database, connection.ServerVersion,
            DateTimeOffset.UtcNow, objects, history, warnings.AsReadOnly(), Coverage)
        { Provider = DatabaseProvider.SqlServer };
    }

    private static async Task CheckMetadataAccessAsync(SqlConnection connection, CancellationToken token)
    {
        await using var command = new SqlCommand("""
            SELECT CONVERT(int, SERVERPROPERTY('ProductMajorVersion')),
                CONVERT(int, SERVERPROPERTY('EngineEdition')),
                COALESCE(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION'), 0),
                CASE WHEN EXISTS (
                    SELECT 1 FROM sys.database_permissions p
                    JOIN sys.user_token t ON t.principal_id = p.grantee_principal_id
                    WHERE p.state = 'D' AND p.permission_name IN ('VIEW DEFINITION', 'CONTROL')
                        AND p.class IN (0, 1, 3, 6)
                ) THEN 1 ELSE 0 END;
            """, connection) { CommandTimeout = 35 };
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new InvalidOperationException(PermissionError);
        var major = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
        var edition = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
        if (major < 13 && edition != 5) throw new InvalidOperationException(VersionError);
        if (reader.GetInt32(2) != 1 || reader.GetInt32(3) != 0)
            throw new InvalidOperationException(PermissionError);
    }

    private static async Task<IReadOnlyList<DatabaseSchemaObject>> ReadObjectsAsync(SqlConnection connection,
        CancellationToken token)
    {
        await using (var validation = new SqlCommand(DefinitionValidation, connection) { CommandTimeout = 35 })
        {
            var incomplete = await validation.ExecuteScalarAsync(token);
            if (Convert.ToInt32(incomplete, CultureInfo.InvariantCulture) != 0)
                throw new InvalidOperationException(DefinitionError);
        }
        var objects = new List<DatabaseSchemaObject>();
        long characterCount = 0;
        foreach (var query in CatalogQueries)
        {
            await using var command = new SqlCommand(CatalogCte + query, connection) { CommandTimeout = 35 };
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                if (objects.Count >= MaximumObjects || reader.IsDBNull(3) || reader.GetBoolean(4))
                    throw new InvalidOperationException(reader.IsDBNull(3) ? DefinitionError : SizeError);
                var definition = reader.GetString(3);
                characterCount += definition.Length;
                if (definition.Length > MaximumDefinitionLength || characterCount > MaximumSnapshotCharacters)
                    throw new InvalidOperationException(SizeError);
                objects.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), definition));
            }
        }
        var sorted = objects.OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Schema, StringComparer.Ordinal).ThenBy(item => item.Name, StringComparer.Ordinal).ToArray();
        if (sorted.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count() != sorted.Length)
            throw new InvalidOperationException(IdentityError);
        return Array.AsReadOnly(sorted);
    }

    private static async Task<DatabaseMigrationHistory> ReadMigrationHistoryAsync(SqlConnection connection,
        CancellationToken token)
    {
        const string unavailable = "EF migration history is unreadable, has an unsupported shape or uses row security or masked history columns. Schema comparison remains available; migration status is unknown.";
        try
        {
            var matches = new List<(string Schema, string Name, bool Readable)>();
            await using (var command = new SqlCommand("""
                SELECT TOP (2) s.name, o.name,
                    CONVERT(bit, CASE WHEN o.type = 'U'
                        AND HAS_PERMS_BY_NAME(QUOTENAME(s.name) + '.' + QUOTENAME(o.name), 'OBJECT', 'SELECT') = 1
                        AND NOT EXISTS (SELECT 1 FROM sys.security_predicates p WHERE p.target_object_id = o.object_id)
                        AND NOT EXISTS (SELECT 1 FROM sys.masked_columns c WHERE c.object_id = o.object_id
                            AND c.is_masked = 1
                            AND c.name COLLATE Latin1_General_100_BIN2 IN (N'MigrationId', N'ProductVersion'))
                        AND (SELECT COUNT(*) FROM sys.columns c WHERE c.object_id = o.object_id
                            AND c.name COLLATE Latin1_General_100_BIN2 IN (N'MigrationId', N'ProductVersion')
                            AND c.system_type_id IN (167, 175, 231, 239) AND c.is_computed = 0
                            AND c.encryption_type IS NULL) = 2
                        THEN 1 ELSE 0 END)
                FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id
                WHERE o.name COLLATE Latin1_General_100_BIN2 = N'__EFMigrationsHistory'
                    AND o.type IN ('U', 'V', 'SN') AND o.is_ms_shipped = 0
                    AND s.name NOT IN ('sys', 'INFORMATION_SCHEMA')
                ORDER BY s.name COLLATE Latin1_General_100_BIN2;
                """, connection) { CommandTimeout = 35 })
            await using (var reader = await command.ExecuteReaderAsync(token))
                while (await reader.ReadAsync(token)) matches.Add((reader.GetString(0), reader.GetString(1), reader.GetBoolean(2)));
            if (matches.Count == 0)
                return new(true, null, [], "No standard EF migration-history table was found. No recorded EF IDs were found; custom history-table names are not detected.");
            if (matches.Count != 1)
                return new(false, null, [], "Multiple EF migration-history tables were found. Migration status is ambiguous; schema comparison remains available.");
            var table = matches[0];
            var tableName = $"{Quote(table.Schema)}.{Quote(table.Name)}";
            if (!table.Readable) return new(false, tableName, [], unavailable);
            var ids = new List<string>();
            await using (var command = new SqlCommand($"SELECT TOP ({MaximumMigrations + 1}) LEFT(CONVERT(nvarchar(max), [MigrationId]), 257) FROM {tableName} ORDER BY [MigrationId] COLLATE Latin1_General_100_BIN2;", connection) { CommandTimeout = 35 })
            await using (var reader = await command.ExecuteReaderAsync(token))
            {
                while (await reader.ReadAsync(token))
                {
                    if (reader.IsDBNull(0)) return new(false, tableName, [], "EF migration history contains invalid IDs. Schema comparison remains available.");
                    var id = reader.GetString(0);
                    if (id.Length is 0 or > 256 || ids.Count >= MaximumMigrations)
                        return new(false, tableName, [], "EF migration history exceeds supported limits or contains invalid IDs. Schema comparison remains available.");
                    ids.Add(id);
                }
            }
            if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
                return new(false, tableName, [], "EF migration history contains duplicate IDs. Schema comparison remains available.");
            return new(true, tableName, ids.AsReadOnly(), null);
        }
        catch (SqlException) when (!token.IsCancellationRequested)
        {
            return new(false, null, [], unavailable);
        }
    }

    private static string Quote(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    internal static bool IsKnownError(string message) => message is VersionError or PermissionError or DefinitionError or SizeError or IdentityError;

    public static string DescribeError(Exception exception) => exception switch
    {
        InvalidOperationException when IsKnownError(exception.Message) => exception.Message,
        OperationCanceledException => "The SQL Server operation was canceled or timed out.",
        SqlException { Number: -2 or 1222 } => "The SQL Server operation timed out. Check connectivity or retry when database locks have cleared.",
        SqlException { Number: 18456 or 18452 } => "SQL Server sign-in failed. Check the selected authentication method and credentials.",
        SqlException { Number: 229 or 230 or 297 } => "SQL Server denied metadata access. Check database permissions, including VIEW DEFINITION.",
        _ => "SQL Server metadata could not be read. Check the server, database, authentication, permissions and trusted TLS certificate."
    };

    // These checks detect missing definitions rather than silently comparing NULL as an empty string.
    private const string DefinitionValidation = """
        SELECT CASE WHEN EXISTS (
            SELECT 1 FROM sys.sql_modules m JOIN sys.objects o ON o.object_id = m.object_id
            WHERE o.is_ms_shipped = 0 AND m.definition IS NULL
        ) OR EXISTS (
            SELECT 1 FROM sys.objects o
            LEFT JOIN sys.sql_modules m ON m.object_id = o.object_id
            WHERE o.is_ms_shipped = 0 AND o.type IN ('V', 'P', 'FN', 'IF', 'TF', 'TR', 'RF', 'R')
                AND (m.object_id IS NULL OR m.definition IS NULL)
        ) OR EXISTS (
            SELECT 1 FROM sys.computed_columns c JOIN sys.objects o ON o.object_id = c.object_id
            WHERE o.is_ms_shipped = 0 AND c.definition IS NULL
        ) OR EXISTS (
            SELECT 1 FROM sys.default_constraints c WHERE c.is_ms_shipped = 0 AND c.definition IS NULL
        ) OR EXISTS (
            SELECT 1 FROM sys.check_constraints c WHERE c.is_ms_shipped = 0 AND c.definition IS NULL
        ) OR EXISTS (
            SELECT 1 FROM sys.indexes i JOIN sys.objects o ON o.object_id = i.object_id
            WHERE o.is_ms_shipped = 0 AND i.has_filter = 1 AND i.filter_definition IS NULL
        ) THEN 1 ELSE 0 END;
        """;

    private const string CatalogCte = """
        WITH user_schemas AS (
            SELECT s.* FROM sys.schemas s WHERE s.name NOT IN ('sys', 'INFORMATION_SCHEMA')
        ), user_relations AS (
            SELECT o.object_id, o.type, s.name AS schema_name, COALESCE(tt.name, o.name) AS name,
                o.principal_id, CASE WHEN tt.user_type_id IS NULL THEN 0 ELSE 1 END AS is_table_type
            FROM sys.objects o JOIN user_schemas s ON s.schema_id = o.schema_id
            LEFT JOIN sys.table_types tt ON tt.type_table_object_id = o.object_id
            WHERE o.is_ms_shipped = 0 AND o.type IN ('U', 'V', 'TT')
        ), entries AS (
        """;

    private static string Bounded(string sql) => sql + $"""
        ) SELECT TOP ({MaximumObjects + 1}) kind, schema_name, object_name,
            LEFT(definition, {MaximumDefinitionLength}),
            CONVERT(bit, CASE WHEN DATALENGTH(definition) > {MaximumDefinitionLength * 2} THEN 1 ELSE 0 END)
        FROM entries;
        """;

    // Single-row JSON retains exact values and escaping. Object IDs are used only for joins, never comparison identities.
    private static readonly string[] CatalogQueries =
    [
        Bounded("""
            SELECT 'Database defaults' AS kind, N'' AS schema_name, N'Database' AS object_name,
                (SELECT d.collation_name AS [Collation], d.compatibility_level AS [Compatibility level]
                 FOR JSON PATH, INCLUDE_NULL_VALUES, WITHOUT_ARRAY_WRAPPER) AS definition
            FROM sys.databases d WHERE d.database_id = DB_ID()
            UNION ALL
            SELECT 'Schema', s.name, QUOTENAME(s.name),
                (SELECT USER_NAME(s.principal_id) AS [Owner] FOR JSON PATH, INCLUDE_NULL_VALUES, WITHOUT_ARRAY_WRAPPER)
            FROM user_schemas s
            UNION ALL
            SELECT 'Table', r.schema_name, QUOTENAME(r.name),
                (SELECT USER_NAME(r.principal_id) AS [Explicit owner], t.lock_escalation_desc AS [Lock escalation],
                    t.is_memory_optimized AS [Memory optimized], t.durability_desc AS [Durability],
                    t.temporal_type_desc AS [Temporal kind],
                    QUOTENAME(OBJECT_SCHEMA_NAME(t.history_table_id)) + '.' + QUOTENAME(OBJECT_NAME(t.history_table_id)) AS [History table],
                    t.is_filetable AS [File table], t.uses_ansi_nulls AS [ANSI nulls]
                 FOR JSON PATH, INCLUDE_NULL_VALUES, WITHOUT_ARRAY_WRAPPER)
            FROM user_relations r JOIN sys.tables t ON t.object_id = r.object_id
            UNION ALL
            SELECT CASE WHEN r.is_table_type = 1 THEN 'Type column' ELSE 'Column' END,
                r.schema_name, QUOTENAME(r.name) + '.' + QUOTENAME(c.name),
                (SELECT c.column_id AS [Position], QUOTENAME(SCHEMA_NAME(ty.schema_id)) + '.' + QUOTENAME(ty.name) AS [Type],
                    c.max_length AS [Maximum bytes], c.precision AS [Precision], c.scale AS [Scale],
                    c.is_nullable AS [Nullable], c.collation_name AS [Collation], c.is_ansi_padded AS [ANSI padded],
                    c.is_rowguidcol AS [Row GUID], c.is_sparse AS [Sparse], c.is_column_set AS [Column set],
                    c.is_filestream AS [FILESTREAM], c.is_hidden AS [Hidden], c.generated_always_type_desc AS [Generated always],
                    c.encryption_type_desc AS [Encryption], c.encryption_algorithm_name AS [Encryption algorithm],
                    c.is_identity AS [Identity], CONVERT(nvarchar(128), ic.seed_value) AS [Identity seed],
                    CONVERT(nvarchar(128), ic.increment_value) AS [Identity increment], ic.is_not_for_replication AS [Identity not for replication],
                    cc.definition AS [Computed expression], cc.is_persisted AS [Persisted],
                    dc.name AS [Default constraint], dc.definition AS [Default expression],
                    OBJECT_SCHEMA_NAME(c.rule_object_id) AS [Bound rule schema], OBJECT_NAME(c.rule_object_id) AS [Bound rule],
                    CASE WHEN dc.object_id IS NULL THEN OBJECT_SCHEMA_NAME(c.default_object_id) END AS [Bound default schema],
                    CASE WHEN dc.object_id IS NULL THEN OBJECT_NAME(c.default_object_id) END AS [Bound default]
                 FOR JSON PATH, INCLUDE_NULL_VALUES, WITHOUT_ARRAY_WRAPPER)
            FROM user_relations r JOIN sys.columns c ON c.object_id = r.object_id
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
            LEFT JOIN sys.identity_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
            """),
        Bounded("""
            SELECT CASE WHEN r.is_table_type = 1 THEN 'Type key' ELSE 'Key constraint' END AS kind,
                r.schema_name, QUOTENAME(r.name) + '.' + QUOTENAME(k.name) AS object_name,
                (SELECT k.type_desc AS [Kind], i.name AS [Index], k.is_system_named AS [System named]
                 FOR JSON PATH, INCLUDE_NULL_VALUES, WITHOUT_ARRAY_WRAPPER) AS definition
            FROM user_relations r JOIN sys.key_constraints k ON k.parent_object_id = r.object_id
            JOIN sys.indexes i ON i.object_id = r.object_id AND i.index_id = k.unique_index_id
            UNION ALL
            SELECT 'Foreign key', r.schema_name, QUOTENAME(r.name) + '.' + QUOTENAME(f.name),
                (SELECT QUOTENAME(OBJECT_SCHEMA_NAME(f.referenced_object_id)) + '.' + QUOTENAME(OBJECT_NAME(f.referenced_object_id)) AS [Referenced table],
                    f.delete_referential_action_desc AS [On delete], f.update_referential_action_desc AS [On update],
                    f.is_disabled AS [Disabled], f.is_not_trusted AS [Not trusted], f.is_not_for_replication AS [Not for replication],
                    f.is_system_named AS [System named]
                 FOR JSON PATH, INCLUDE_NULL_VALUES, WITHOUT_ARRAY_WRAPPER)
            FROM user_relations r JOIN sys.foreign_keys f ON f.parent_object_id = r.object_id
            UNION ALL
            SELECT 'Foreign key column', r.schema_name,
                QUOTENAME(r.name) + '.' + QUOTENAME(f.name) + '.' + CONVERT(nvarchar(10), fc.constraint_column_id),
                (SELECT COL_NAME(fc.parent_object_id, fc.parent_column_id) AS [Column],
                    COL_NAME(fc.referenced_object_id, fc.referenced_column_id) AS [Referenced column]
                 FOR JSON PATH, INCLUDE_NULL_VALUES, WITHOUT_ARRAY_WRAPPER)
            FROM user_relations r JOIN sys.foreign_keys f ON f.parent_object_id = r.object_id
            JOIN sys.foreign_key_columns fc ON fc.constraint_object_id = f.object_id
            UNION ALL
            SELECT CASE WHEN r.is_table_type = 1 THEN 'Type check constraint' ELSE 'Check constraint' END,
                r.schema_name, QUOTENAME(r.name) + '.' + QUOTENAME(c.name),
                (SELECT c.definition AS [Expression], COL_NAME(c.parent_object_id, c.parent_column_id) AS [Column],
                    c.is_disabled AS [Disabled], c.is_not_trusted AS [Not trusted], c.is_not_for_replication AS [Not for replication],
                    c.uses_database_collation AS [Uses database collation], c.is_system_named AS [System named]
                 FOR JSON PATH, INCLUDE_NULL_VALUES, WITHOUT_ARRAY_WRAPPER)
            FROM user_relations r JOIN sys.check_constraints c ON c.parent_object_id = r.object_id
            UNION ALL
            SELECT CASE WHEN r.is_table_type = 1 THEN 'Type index' ELSE 'Index' END,
                r.schema_name, QUOTENAME(r.name) + '.' + QUOTENAME(i.name),
                (SELECT i.type_desc AS [Kind], i.is_unique AS [Unique], i.is_primary_key AS [Primary key],
                    i.is_unique_constraint AS [Unique constraint], i.is_disabled AS [Disabled], i.is_hypothetical AS [Hypothetical],
                    i.has_filter AS [Filtered], i.filter_definition AS [Filter], i.fill_factor AS [Fill factor],
                    i.is_padded AS [Padded], i.ignore_dup_key AS [Ignore duplicate key],
                    i.allow_row_locks AS [Allow row locks], i.allow_page_locks AS [Allow page locks]
                 FOR JSON PATH, INCLUDE_NULL_VALUES, WITHOUT_ARRAY_WRAPPER)
            FROM user_relations r JOIN sys.indexes i ON i.object_id = r.object_id WHERE i.index_id > 0
            UNION ALL
            SELECT CASE WHEN r.is_table_type = 1 THEN 'Type index column' ELSE 'Index column' END,
                r.schema_name, QUOTENAME(r.name) + '.' + QUOTENAME(i.name) + '.' + CONVERT(nvarchar(10), ic.index_column_id),
                (SELECT COL_NAME(ic.object_id, ic.column_id) AS [Column], ic.key_ordinal AS [Key position],
                    ic.is_descending_key AS [Descending], ic.is_included_column AS [Included], ic.partition_ordinal AS [Partition position]
                 FOR JSON PATH, INCLUDE_NULL_VALUES, WITHOUT_ARRAY_WRAPPER)
            FROM user_relations r JOIN sys.indexes i ON i.object_id = r.object_id
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id WHERE i.index_id > 0
            """),
        Bounded("""
            SELECT CASE o.type WHEN 'V' THEN 'View' WHEN 'P' THEN 'Procedure' WHEN 'RF' THEN 'Procedure' WHEN 'TR' THEN 'Trigger'
                    WHEN 'R' THEN 'Rule' WHEN 'D' THEN 'Bound default' ELSE 'Function' END AS kind,
                s.name AS schema_name, QUOTENAME(o.name) AS object_name,
                (SELECT o.type_desc AS [Kind], USER_NAME(o.principal_id) AS [Explicit owner], m.definition AS [Definition],
                    m.uses_ansi_nulls AS [ANSI nulls], m.uses_quoted_identifier AS [Quoted identifiers],
                    m.is_schema_bound AS [Schema bound], m.uses_database_collation AS [Uses database collation],
                    m.is_recompiled AS [Recompiled], m.null_on_null_input AS [Null on null input],
                    m.uses_native_compilation AS [Native compilation],
                    CASE m.execute_as_principal_id WHEN -2 THEN N'OWNER' ELSE USER_NAME(m.execute_as_principal_id) END AS [Execute as],
                    tr.is_disabled AS [Trigger disabled], tr.is_instead_of_trigger AS [Instead of trigger],
                    tr.is_not_for_replication AS [Not for replication],
                    QUOTENAME(OBJECT_SCHEMA_NAME(tr.parent_id)) + '.' + QUOTENAME(OBJECT_NAME(tr.parent_id)) AS [Trigger table]
                 FOR JSON PATH, INCLUDE_NULL_VALUES, WITHOUT_ARRAY_WRAPPER) AS definition
            FROM sys.objects o JOIN user_schemas s ON s.schema_id = o.schema_id
            JOIN sys.sql_modules m ON m.object_id = o.object_id
            LEFT JOIN sys.triggers tr ON tr.object_id = o.object_id
            WHERE o.is_ms_shipped = 0
            UNION ALL
            SELECT 'Sequence', s.name, QUOTENAME(q.name),
                (SELECT QUOTENAME(SCHEMA_NAME(ty.schema_id)) + '.' + QUOTENAME(ty.name) AS [Type],
                    CONVERT(nvarchar(128), q.start_value) AS [Start], CONVERT(nvarchar(128), q.increment) AS [Increment],
                    CONVERT(nvarchar(128), q.minimum_value) AS [Minimum], CONVERT(nvarchar(128), q.maximum_value) AS [Maximum],
                    q.precision AS [Precision], q.scale AS [Scale], q.is_cycling AS [Cycle],
                    q.is_cached AS [Cached], q.cache_size AS [Cache size], USER_NAME(q.principal_id) AS [Explicit owner]
                 FOR JSON PATH, INCLUDE_NULL_VALUES, WITHOUT_ARRAY_WRAPPER)
            FROM sys.sequences q JOIN user_schemas s ON s.schema_id = q.schema_id
            JOIN sys.types ty ON ty.user_type_id = q.user_type_id WHERE q.is_ms_shipped = 0
            UNION ALL
            SELECT 'Synonym', s.name, QUOTENAME(sn.name),
                (SELECT sn.base_object_name AS [Target], USER_NAME(sn.principal_id) AS [Explicit owner]
                 FOR JSON PATH, INCLUDE_NULL_VALUES, WITHOUT_ARRAY_WRAPPER)
            FROM sys.synonyms sn JOIN user_schemas s ON s.schema_id = sn.schema_id WHERE sn.is_ms_shipped = 0
            UNION ALL
            SELECT CASE WHEN ty.is_table_type = 1 THEN 'Table type' ELSE 'Alias type' END, s.name, QUOTENAME(ty.name),
                (SELECT TYPE_NAME(ty.system_type_id) AS [Base type], ty.max_length AS [Maximum bytes],
                    ty.precision AS [Precision], ty.scale AS [Scale], ty.collation_name AS [Collation],
                    ty.is_nullable AS [Nullable], USER_NAME(ty.principal_id) AS [Explicit owner],
                    OBJECT_SCHEMA_NAME(ty.default_object_id) AS [Bound default schema], OBJECT_NAME(ty.default_object_id) AS [Bound default],
                    OBJECT_SCHEMA_NAME(ty.rule_object_id) AS [Bound rule schema], OBJECT_NAME(ty.rule_object_id) AS [Bound rule]
                 FOR JSON PATH, INCLUDE_NULL_VALUES, WITHOUT_ARRAY_WRAPPER)
            FROM sys.types ty JOIN user_schemas s ON s.schema_id = ty.schema_id
            WHERE ty.is_user_defined = 1 AND ty.is_assembly_type = 0
            """)
    ];
}
