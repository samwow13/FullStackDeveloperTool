using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FullStackLauncher.Models;
using Npgsql;

namespace FullStackLauncher.Services;

/// <summary>Compares metadata, never generates or executes synchronization SQL.</summary>
public static class PostgresSchemaComparer
{
    private const int MaximumObjects = 100_000;
    private const int MaximumDefinitionLength = 1_048_576;
    private const long MaximumSnapshotCharacters = 32_000_000;
    private const int MaximumMigrations = 20_000;

    public static IReadOnlyList<string> Coverage { get; } = Array.AsReadOnly(new[]
    {
        "Compares non-system PostgreSQL schemas; tables and partitions; columns, defaults, identities and generated expressions; constraints; indexes; views; materialized views; sequences; routines; enums, domains, composite and range types; collations; extensions; triggers; rules; RLS policies; object owners and ACLs; database grants; default privileges; comments; and database encoding/collation defaults.",
        "Reads catalog metadata and EF migration IDs only. Table contents, row counts, sequence current values, materialized-view contents and physical storage/statistics are not compared.",
        "Extension membership is compared by extension name/version/schema; extension-owned implementation objects are excluded. Custom base types, operators, casts, aggregates, operator classes/families, text-search objects, foreign-table/server/user-mapping options, large objects, role definitions/memberships, tablespace configuration, publications/subscriptions, event triggers, security labels and server/database configuration overrides are outside coverage.",
        "Owners, grants and tablespace names may intentionally differ by environment. ACLs represent direct/default object grants, not effective access through role inheritance. Database encoding/collation and grants are compared; database names are identity details, and database ownership is reflected only through grants.",
        "Each database is captured in a separate consistent read-only transaction. Captures are not a shared point in time. Definition formatting and system defaults can differ across PostgreSQL versions. No differences means the captured metadata agrees within this coverage; it does not prove migrations or application data are equivalent.",
        "EF history discovery supports the standard __EFMigrationsHistory table in any non-system schema. Renamed history tables are outside discovery; multiple matching tables or unreadable history prevent a reliable migration-history comparison. Applied IDs do not verify historical migration file contents."
    });

    public static async Task<DatabaseSchemaSnapshot> CaptureAsync(DatabaseConnectionSource source, string database,
        CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var requestToken = timeout.Token;
        await using var connection = source.CreateConnection(database);
        await connection.OpenAsync(requestToken);
        if (connection.PostgreSqlVersion.Major < 12)
            throw new InvalidOperationException("Schema comparison requires PostgreSQL 12 or later.");
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, requestToken);
        // Fixed catalog search_path makes deparsed expressions independent of role search_path settings.
        await using (var setup = new NpgsqlCommand("""
            SET TRANSACTION READ ONLY;
            SET LOCAL search_path = pg_catalog;
            SET LOCAL statement_timeout = '30s';
            SET LOCAL lock_timeout = '3s';
            SET LOCAL row_security = off;
            """, connection, transaction))
            await setup.ExecuteNonQueryAsync(requestToken);

        var objects = await ReadObjectsAsync(connection, transaction, requestToken);
        var history = await ReadMigrationHistoryAsync(connection, transaction, requestToken);
        var warnings = new List<string>();
        if (history.Warning is { Length: > 0 } warning) warnings.Add(warning);
        await transaction.CommitAsync(requestToken);
        return new(source.Id, source.Server, connection.Database, connection.PostgreSqlVersion.ToString(),
            DateTimeOffset.UtcNow, objects, history, warnings.AsReadOnly(), Coverage);
    }

    /// <summary>Stable hash of covered catalog definitions; migration history is checked separately.</summary>
    public static string Fingerprint(DatabaseSchemaSnapshot snapshot) => Fingerprint(snapshot.Objects);

    /// <summary>Read the same catalog fingerprint inside an existing migration transaction.</summary>
    internal static async Task<string> ReadFingerprintAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken token)
    {
        if (connection.PostgreSqlVersion.Major < 12)
            throw new InvalidOperationException("Schema comparison requires PostgreSQL 12 or later.");
        await transaction.SaveAsync("launcher_fingerprint", token);
        // Rolling back this savepoint restores caller settings and leaves earlier migration locks/reads intact.
        try
        {
            await using (var setup = new NpgsqlCommand("""
                SET LOCAL search_path = pg_catalog;
                SET LOCAL statement_timeout = '30s';
                SET LOCAL lock_timeout = '3s';
                """, connection, transaction))
                await setup.ExecuteNonQueryAsync(token);
            return Fingerprint(await ReadObjectsAsync(connection, transaction, token));
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                await transaction.RollbackAsync("launcher_fingerprint", token);
                await transaction.ReleaseAsync("launcher_fingerprint", token);
            }
        }
    }

    private static string Fingerprint(IReadOnlyList<DatabaseSchemaObject> objects)
    {
        var canonical = objects.OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Schema, StringComparer.Ordinal).ThenBy(item => item.Name, StringComparer.Ordinal)
            .Select(item => new[] { item.Kind, item.Schema, item.Name, item.Definition });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical))));
    }

    private static async Task<IReadOnlyList<DatabaseSchemaObject>> ReadObjectsAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, CancellationToken token)
    {
        var objects = new List<DatabaseSchemaObject>();
        long characterCount = 0;
        foreach (var query in CatalogQueries)
        {
            await using var command = new NpgsqlCommand(CatalogCte + query, connection, transaction);
            command.CommandTimeout = 35;
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                if (objects.Count >= MaximumObjects || reader.GetBoolean(4))
                    throw new InvalidOperationException("The schema exceeds the comparison size limit. No partial comparison was produced.");
                var definition = reader.GetString(3);
                characterCount += definition.Length;
                if (characterCount > MaximumSnapshotCharacters)
                    throw new InvalidOperationException("The schema exceeds the comparison size limit. No partial comparison was produced.");
                objects.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), definition));
            }
        }

        var sorted = objects.OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Schema, StringComparer.Ordinal).ThenBy(item => item.Name, StringComparer.Ordinal).ToArray();
        if (sorted.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count() != sorted.Length)
            throw new InvalidOperationException("The database contains ambiguous object identities. No partial comparison was produced.");
        return Array.AsReadOnly(sorted);
    }

    public static DatabaseSchemaComparison Compare(DatabaseSchemaSnapshot source, DatabaseSchemaSnapshot target)
    {
        var sourceObjects = source.Objects.ToDictionary(item => item.Key, StringComparer.Ordinal);
        var targetObjects = target.Objects.ToDictionary(item => item.Key, StringComparer.Ordinal);
        var differences = new List<DatabaseSchemaDifference>();
        var unchanged = 0;
        foreach (var key in sourceObjects.Keys.Union(targetObjects.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            sourceObjects.TryGetValue(key, out var sourceObject);
            targetObjects.TryGetValue(key, out var targetObject);
            if (sourceObject is not null && targetObject is not null &&
                string.Equals(sourceObject.Definition, targetObject.Definition, StringComparison.Ordinal))
            {
                unchanged++;
                continue;
            }
            var identity = sourceObject ?? targetObject!;
            differences.Add(new(identity.Kind, identity.Schema, identity.Name,
                sourceObject is null ? DatabaseSchemaChange.TargetOnly : targetObject is null
                    ? DatabaseSchemaChange.SourceOnly : DatabaseSchemaChange.Changed,
                sourceObject?.Definition, targetObject?.Definition));
        }
        var historiesAvailable = source.MigrationHistory.IsAvailable && target.MigrationHistory.IsAvailable;
        return new(source, target, differences.AsReadOnly(), unchanged,
            historiesAvailable ? source.MigrationHistory.MigrationIds.Except(target.MigrationHistory.MigrationIds, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() : [],
            historiesAvailable ? target.MigrationHistory.MigrationIds.Except(source.MigrationHistory.MigrationIds, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() : []);
    }

    public static string DescribeError(Exception exception) => exception switch
    {
        InvalidOperationException { Message: "Schema comparison requires PostgreSQL 12 or later." } => "Schema comparison requires PostgreSQL 12 or later.",
        InvalidOperationException { Message: "The schema exceeds the comparison size limit. No partial comparison was produced." } => "The schema exceeds the comparison size limit. No partial comparison was produced.",
        InvalidOperationException { Message: "The database contains ambiguous object identities. No partial comparison was produced." } => "The database contains ambiguous object identities. No partial comparison was produced.",
        _ => PostgresSchemaReader.DescribeError(exception)
    };

    private static async Task<DatabaseMigrationHistory> ReadMigrationHistoryAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, CancellationToken token)
    {
        await transaction.SaveAsync("launcher_history", token);
        try
        {
            var matches = new List<(string Schema, string Name, bool Readable)>();
            await using (var command = new NpgsqlCommand("""
                SELECT n.nspname, c.relname,
                    c.relkind = 'r' AND NOT c.relrowsecurity
                    AND pg_catalog.has_schema_privilege(n.oid, 'USAGE')
                    AND pg_catalog.has_table_privilege(c.oid, 'SELECT')
                    AND (SELECT count(*) = 2 FROM pg_catalog.pg_attribute a
                         WHERE a.attrelid = c.oid AND a.attname IN ('MigrationId', 'ProductVersion')
                           AND a.attnum > 0 AND NOT a.attisdropped AND a.atttypid IN (25, 1043))
                FROM pg_catalog.pg_class c
                JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                WHERE c.relname = '__EFMigrationsHistory' AND c.relkind IN ('r', 'p', 'v', 'm', 'f')
                  AND n.nspname <> 'information_schema' AND n.nspname !~ '^pg_'
                ORDER BY n.nspname LIMIT 2
                """, connection, transaction))
            await using (var reader = await command.ExecuteReaderAsync(token))
                while (await reader.ReadAsync(token)) matches.Add((reader.GetString(0), reader.GetString(1), reader.GetBoolean(2)));

            if (matches.Count == 0)
                return new(true, null, [], "No standard EF migration-history table was found. This means no recorded EF IDs were found; a custom history-table name is not detected.");
            if (matches.Count != 1)
                return new(false, null, [], "Multiple EF migration-history tables were found. Migration status is ambiguous; schema comparison remains available.");
            var table = matches[0];
            using var quoting = new NpgsqlCommandBuilder();
            var tableName = $"{quoting.QuoteIdentifier(table.Schema)}.{quoting.QuoteIdentifier(table.Name)}";
            if (!table.Readable)
                return new(false, tableName, [], "The EF migration-history table is unreadable or has an unsupported shape or row-security policy. Schema comparison remains available.");
            var ids = new List<string>();
            await using (var command = new NpgsqlCommand($"SELECT pg_catalog.left(\"MigrationId\", 257) FROM {tableName} ORDER BY \"MigrationId\" LIMIT {MaximumMigrations + 1}", connection, transaction))
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
        catch (PostgresException) when (!token.IsCancellationRequested)
        {
            await transaction.RollbackAsync("launcher_history", token);
            return new(false, null, [], "EF migration history could not be read. Schema comparison remains available; migration status is unknown.");
        }
    }

    // Extension-owned implementation details are intentionally excluded, as pg_dump does for extension members.
    // Names are always quoted when composed; definitions contain logical names, never database-local object IDs.
    private const string CatalogCte = """
        WITH user_namespaces AS (
            SELECT n.* FROM pg_catalog.pg_namespace n
            WHERE n.nspname <> 'information_schema' AND n.nspname !~ '^pg_'
        ), user_relations AS (
            SELECT c.*, n.nspname FROM pg_catalog.pg_class c
            JOIN user_namespaces n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('r','p','v','m','f','S','c')
              AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_depend d WHERE d.classid = 'pg_catalog.pg_class'::regclass
                  AND d.objid = c.oid AND d.deptype = 'e')
        ), user_types AS (
            SELECT t.*, n.nspname FROM pg_catalog.pg_type t
            JOIN user_namespaces n ON n.oid = t.typnamespace
            WHERE t.typtype IN ('e','d','c','r','m')
              AND (t.typrelid = 0 OR EXISTS (SELECT 1 FROM user_relations c WHERE c.oid = t.typrelid AND c.relkind = 'c'))
              AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_depend d WHERE d.classid = 'pg_catalog.pg_type'::regclass
                  AND d.objid = t.oid AND d.deptype = 'e')
        ), user_routines AS (
            SELECT p.*, n.nspname FROM pg_catalog.pg_proc p JOIN user_namespaces n ON n.oid = p.pronamespace
            WHERE p.prokind IN ('f','p','w')
              AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_depend d WHERE d.classid = 'pg_catalog.pg_proc'::regclass
                  AND d.objid = p.oid AND d.deptype = 'e')
        ), entries AS (
        """;

    private static string Bounded(string sql) => sql + $"""
        ) SELECT kind, schema_name, object_name,
            pg_catalog.left(COALESCE(definition, ''), {MaximumDefinitionLength}),
            pg_catalog.length(COALESCE(definition, '')) > {MaximumDefinitionLength}
        FROM entries LIMIT {MaximumObjects + 1}
        """;

    private static readonly string[] CatalogQueries =
    [
        Bounded("""
            SELECT 'Database defaults' AS kind, ''::text AS schema_name, 'Database'::text AS object_name,
                concat_ws(E'\n', 'Encoding: ' || pg_catalog.pg_encoding_to_char(d.encoding),
                    'Collate: ' || d.datcollate, 'Ctype: ' || d.datctype,
                    'Locale provider: ' || COALESCE(to_jsonb(d)->>'datlocprovider', 'c'),
                    'Locale: ' || COALESCE(to_jsonb(d)->>'datlocale', to_jsonb(d)->>'daticulocale', ''),
                    'ICU rules: ' || COALESCE(to_jsonb(d)->>'daticurules', '')) AS definition
            FROM pg_catalog.pg_database d WHERE d.datname = current_database()
            UNION ALL
            SELECT 'Schema', n.nspname, quote_ident(n.nspname),
                concat_ws(E'\n', 'Owner: ' || pg_catalog.pg_get_userbyid(n.nspowner),
                    'Comment: ' || COALESCE(pg_catalog.obj_description(n.oid, 'pg_namespace'), ''))
            FROM user_namespaces n
            WHERE NOT EXISTS (SELECT 1 FROM pg_catalog.pg_depend d WHERE d.classid = 'pg_catalog.pg_namespace'::regclass
                AND d.objid = n.oid AND d.deptype = 'e')
            UNION ALL
            SELECT CASE c.relkind WHEN 'p' THEN 'Partitioned table' WHEN 'v' THEN 'View'
                    WHEN 'm' THEN 'Materialized view' WHEN 'f' THEN 'Foreign table' ELSE 'Table' END,
                c.nspname, quote_ident(c.relname),
                concat_ws(E'\n', 'Owner: ' || pg_catalog.pg_get_userbyid(c.relowner),
                    'Persistence: ' || c.relpersistence::text, 'Replica identity: ' || c.relreplident::text,
                    'Row security: ' || c.relrowsecurity::text, 'Force row security: ' || c.relforcerowsecurity::text,
                    'Options: ' || COALESCE((SELECT string_agg(option, ', ' ORDER BY option COLLATE "C") FROM unnest(c.reloptions) option), ''),
                    'Tablespace: ' || COALESCE((SELECT s.spcname FROM pg_catalog.pg_tablespace s WHERE s.oid = c.reltablespace), '(database default)'),
                    'Access method: ' || COALESCE((SELECT a.amname FROM pg_catalog.pg_am a WHERE a.oid = c.relam), ''),
                    'Partition key: ' || COALESCE(pg_catalog.pg_get_partkeydef(c.oid), ''),
                    'Partition bound: ' || COALESCE(pg_catalog.pg_get_expr(c.relpartbound, c.oid, false), ''),
                    'Parents: ' || COALESCE((SELECT string_agg(format('%I.%I', pn.nspname, pc.relname), ', ' ORDER BY i.inhseqno)
                        FROM pg_catalog.pg_inherits i JOIN pg_catalog.pg_class pc ON pc.oid = i.inhparent
                        JOIN pg_catalog.pg_namespace pn ON pn.oid = pc.relnamespace WHERE i.inhrelid = c.oid), ''),
                    CASE WHEN c.relkind IN ('v','m') THEN 'Query: ' || pg_catalog.pg_get_viewdef(c.oid, false) END,
                    CASE WHEN c.relkind = 'f' THEN 'Foreign server: ' || COALESCE((SELECT s.srvname
                        FROM pg_catalog.pg_foreign_table f JOIN pg_catalog.pg_foreign_server s ON s.oid = f.ftserver WHERE f.ftrelid = c.oid), '') END,
                    'Comment: ' || COALESCE(pg_catalog.obj_description(c.oid, 'pg_class'), ''))
            FROM user_relations c WHERE c.relkind IN ('r','p','v','m','f')
            UNION ALL
            SELECT 'Column', c.nspname, format('%I.%I', c.relname, a.attname),
                concat_ws(E'\n', 'Position: ' || a.attnum::text, 'Type: ' || pg_catalog.format_type(a.atttypid, a.atttypmod),
                    'Not null: ' || a.attnotnull::text,
                    'Default / generation expression: ' || COALESCE(pg_catalog.pg_get_expr(d.adbin, d.adrelid, false), ''),
                    'Identity: ' || a.attidentity::text, 'Generated: ' || COALESCE(to_jsonb(a)->>'attgenerated', ''),
                    'Collation: ' || COALESCE((SELECT format('%I.%I', cn.nspname, co.collname)
                        FROM pg_catalog.pg_collation co JOIN pg_catalog.pg_namespace cn ON cn.oid = co.collnamespace WHERE co.oid = a.attcollation), ''),
                    'Storage: ' || a.attstorage::text, 'Compression: ' || COALESCE(to_jsonb(a)->>'attcompression', ''),
                    'Local: ' || a.attislocal::text,
                    'Comment: ' || COALESCE(pg_catalog.col_description(c.oid, a.attnum), ''))
            FROM user_relations c JOIN pg_catalog.pg_attribute a ON a.attrelid = c.oid
            LEFT JOIN pg_catalog.pg_attrdef d ON d.adrelid = c.oid AND d.adnum = a.attnum
            WHERE c.relkind IN ('r','p','v','m','f','c') AND a.attnum > 0 AND NOT a.attisdropped
            UNION ALL
            SELECT 'Constraint', c.nspname, format('%I.%I', c.relname, k.conname),
                concat_ws(E'\n', pg_catalog.pg_get_constraintdef(k.oid, false),
                    'Validated: ' || k.convalidated::text, 'Deferrable: ' || k.condeferrable::text,
                    'Initially deferred: ' || k.condeferred::text, 'Local: ' || k.conislocal::text,
                    'Enforced: ' || COALESCE(to_jsonb(k)->>'conenforced', 'true'),
                    'Comment: ' || COALESCE(pg_catalog.obj_description(k.oid, 'pg_constraint'), ''))
            FROM user_relations c JOIN pg_catalog.pg_constraint k ON k.conrelid = c.oid
            UNION ALL
            SELECT 'Index', c.nspname, format('%I.%I', c.relname, idx.relname),
                concat_ws(E'\n', pg_catalog.pg_get_indexdef(i.indexrelid, 0, false),
                    'Valid: ' || i.indisvalid::text, 'Ready: ' || i.indisready::text,
                    'Live: ' || i.indislive::text, 'Clustered: ' || i.indisclustered::text,
                    'Replica identity: ' || i.indisreplident::text,
                    'Nulls not distinct: ' || COALESCE(to_jsonb(i)->>'indnullsnotdistinct', 'false'),
                    'Tablespace: ' || COALESCE((SELECT s.spcname FROM pg_catalog.pg_tablespace s WHERE s.oid = idx.reltablespace), '(database default)'),
                    'Options: ' || COALESCE((SELECT string_agg(option, ', ' ORDER BY option COLLATE "C") FROM unnest(idx.reloptions) option), ''),
                    'Comment: ' || COALESCE(pg_catalog.obj_description(idx.oid, 'pg_class'), ''))
            FROM user_relations c JOIN pg_catalog.pg_index i ON i.indrelid = c.oid
            JOIN pg_catalog.pg_class idx ON idx.oid = i.indexrelid
            WHERE NOT EXISTS (SELECT 1 FROM pg_catalog.pg_depend d WHERE d.classid = 'pg_catalog.pg_class'::regclass
                AND d.objid = idx.oid AND d.deptype = 'e')
            """),
        Bounded("""
            SELECT 'Sequence' AS kind, c.nspname AS schema_name, quote_ident(c.relname) AS object_name,
                concat_ws(E'\n', 'Type: ' || pg_catalog.format_type(s.seqtypid, -1),
                    'Start: ' || s.seqstart::text, 'Increment: ' || s.seqincrement::text,
                    'Minimum: ' || s.seqmin::text, 'Maximum: ' || s.seqmax::text,
                    'Cache: ' || s.seqcache::text, 'Cycle: ' || s.seqcycle::text,
                    'Persistence: ' || c.relpersistence::text,
                    'Owner: ' || pg_catalog.pg_get_userbyid(c.relowner),
                    'Owned by: ' || COALESCE((SELECT string_agg(format('%I.%I.%I (%s)', rn.nspname, rc.relname, a.attname, d.deptype), ', '
                        ORDER BY rn.nspname COLLATE "C", rc.relname COLLATE "C", a.attname COLLATE "C")
                        FROM pg_catalog.pg_depend d JOIN pg_catalog.pg_class rc ON rc.oid = d.refobjid
                        JOIN pg_catalog.pg_namespace rn ON rn.oid = rc.relnamespace
                        JOIN pg_catalog.pg_attribute a ON a.attrelid = rc.oid AND a.attnum = d.refobjsubid
                        WHERE d.classid = 'pg_catalog.pg_class'::regclass AND d.objid = c.oid
                            AND d.refclassid = 'pg_catalog.pg_class'::regclass AND d.deptype IN ('a','i')), ''),
                    'Comment: ' || COALESCE(pg_catalog.obj_description(c.oid, 'pg_class'), '')) AS definition
            FROM user_relations c JOIN pg_catalog.pg_sequence s ON s.seqrelid = c.oid
            UNION ALL
            SELECT CASE p.prokind WHEN 'p' THEN 'Procedure' ELSE 'Function' END,
                p.nspname, quote_ident(p.proname) || '(' || pg_catalog.oidvectortypes(p.proargtypes) || ')',
                concat_ws(E'\n', pg_catalog.pg_get_functiondef(p.oid),
                    'Owner: ' || pg_catalog.pg_get_userbyid(p.proowner),
                    'Comment: ' || COALESCE(pg_catalog.obj_description(p.oid, 'pg_proc'), ''))
            FROM user_routines p
            UNION ALL
            SELECT CASE t.typtype WHEN 'e' THEN 'Enum' WHEN 'd' THEN 'Domain' WHEN 'c' THEN 'Composite type'
                WHEN 'm' THEN 'Multirange type' ELSE 'Range type' END,
                t.nspname, quote_ident(t.typname),
                concat_ws(E'\n', 'Owner: ' || pg_catalog.pg_get_userbyid(t.typowner),
                    CASE WHEN t.typtype = 'e' THEN 'Labels: ' || COALESCE((SELECT string_agg(quote_literal(e.enumlabel), ', ' ORDER BY e.enumsortorder)
                        FROM pg_catalog.pg_enum e WHERE e.enumtypid = t.oid), '') END,
                    CASE WHEN t.typtype = 'd' THEN 'Base type: ' || pg_catalog.format_type(t.typbasetype, t.typtypmod) END,
                    CASE WHEN t.typtype = 'd' THEN 'Not null: ' || t.typnotnull::text END,
                    CASE WHEN t.typtype = 'd' THEN 'Default: ' || COALESCE(pg_catalog.pg_get_expr(t.typdefaultbin, 0, false), t.typdefault, '') END,
                    CASE WHEN t.typcollation <> 0 THEN 'Collation: ' || (SELECT format('%I.%I', cn.nspname, co.collname)
                        FROM pg_catalog.pg_collation co JOIN pg_catalog.pg_namespace cn ON cn.oid = co.collnamespace WHERE co.oid = t.typcollation) END,
                    CASE WHEN t.typtype IN ('r','m') THEN 'Range: ' || COALESCE((SELECT concat_ws('; ',
                        'subtype=' || pg_catalog.format_type(r.rngsubtype, -1),
                        'range_type=' || pg_catalog.format_type(r.rngtypid, -1),
                        'multirange_type=' || CASE WHEN COALESCE(to_jsonb(r)->>'rngmultitypid','0') <> '0'
                            THEN pg_catalog.format_type((to_jsonb(r)->>'rngmultitypid')::oid, -1) ELSE '' END,
                        'collation=' || COALESCE((SELECT format('%I.%I', cn.nspname, co.collname)
                            FROM pg_catalog.pg_collation co JOIN pg_catalog.pg_namespace cn ON cn.oid = co.collnamespace WHERE co.oid = r.rngcollation), ''),
                        'opclass=' || (SELECT format('%I.%I', opn.nspname, op.opcname)
                            FROM pg_catalog.pg_opclass op JOIN pg_catalog.pg_namespace opn ON opn.oid = op.opcnamespace WHERE op.oid = r.rngsubopc),
                        'canonical=' || r.rngcanonical::regprocedure::text, 'subdiff=' || r.rngsubdiff::regprocedure::text)
                        FROM pg_catalog.pg_range r WHERE r.rngtypid = t.oid OR (to_jsonb(r)->>'rngmultitypid')::oid = t.oid), '') END,
                    'Comment: ' || COALESCE(pg_catalog.obj_description(t.oid, 'pg_type'), ''))
            FROM user_types t
            UNION ALL
            SELECT 'Domain constraint', t.nspname, format('%I.%I', t.typname, k.conname),
                concat_ws(E'\n', pg_catalog.pg_get_constraintdef(k.oid, false), 'Validated: ' || k.convalidated::text,
                    'Comment: ' || COALESCE(pg_catalog.obj_description(k.oid, 'pg_constraint'), ''))
            FROM user_types t JOIN pg_catalog.pg_constraint k ON k.contypid = t.oid
            UNION ALL
            SELECT 'Extension', ''::text, quote_ident(e.extname),
                concat_ws(E'\n', 'Version: ' || e.extversion, 'Schema: ' || quote_ident(n.nspname),
                    'Owner: ' || pg_catalog.pg_get_userbyid(e.extowner), 'Relocatable: ' || e.extrelocatable::text,
                    'Comment: ' || COALESCE(pg_catalog.obj_description(e.oid, 'pg_extension'), ''))
            FROM pg_catalog.pg_extension e JOIN pg_catalog.pg_namespace n ON n.oid = e.extnamespace
            UNION ALL
            SELECT 'Collation', n.nspname, quote_ident(c.collname) || ' (encoding ' || c.collencoding::text || ')',
                concat_ws(E'\n', 'Owner: ' || pg_catalog.pg_get_userbyid(c.collowner),
                    'Provider: ' || c.collprovider::text, 'Encoding: ' || CASE WHEN c.collencoding = -1 THEN 'Any' ELSE pg_catalog.pg_encoding_to_char(c.collencoding) END,
                    'Deterministic: ' || COALESCE(to_jsonb(c)->>'collisdeterministic', 'true'),
                    'Collate: ' || COALESCE(c.collcollate, ''), 'Ctype: ' || COALESCE(c.collctype, ''),
                    'Locale: ' || COALESCE(to_jsonb(c)->>'colllocale', to_jsonb(c)->>'colliculocale', ''),
                    'ICU rules: ' || COALESCE(to_jsonb(c)->>'collicurules', ''),
                    'Recorded version: ' || COALESCE(c.collversion, ''),
                    'Comment: ' || COALESCE(pg_catalog.obj_description(c.oid, 'pg_collation'), ''))
            FROM pg_catalog.pg_collation c JOIN user_namespaces n ON n.oid = c.collnamespace
            WHERE NOT EXISTS (SELECT 1 FROM pg_catalog.pg_depend d WHERE d.classid = 'pg_catalog.pg_collation'::regclass
                AND d.objid = c.oid AND d.deptype = 'e')
            """),
        Bounded("""
            SELECT 'Trigger' AS kind, c.nspname AS schema_name, format('%I.%I', c.relname, t.tgname) AS object_name,
                concat_ws(E'\n', pg_catalog.pg_get_triggerdef(t.oid, false), 'Enabled: ' || t.tgenabled::text,
                    'Comment: ' || COALESCE(pg_catalog.obj_description(t.oid, 'pg_trigger'), '')) AS definition
            FROM user_relations c JOIN pg_catalog.pg_trigger t ON t.tgrelid = c.oid
            WHERE NOT t.tgisinternal
              AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_depend d WHERE d.classid = 'pg_catalog.pg_trigger'::regclass
                  AND d.objid = t.oid AND d.deptype = 'e')
            UNION ALL
            SELECT 'Rule', c.nspname, format('%I.%I', c.relname, r.rulename),
                concat_ws(E'\n', pg_catalog.pg_get_ruledef(r.oid, false), 'Enabled: ' || r.ev_enabled::text,
                    'Comment: ' || COALESCE(pg_catalog.obj_description(r.oid, 'pg_rewrite'), ''))
            FROM user_relations c JOIN pg_catalog.pg_rewrite r ON r.ev_class = c.oid
            WHERE r.rulename <> '_RETURN'
              AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_depend d WHERE d.classid = 'pg_catalog.pg_rewrite'::regclass
                  AND d.objid = r.oid AND d.deptype = 'e')
            UNION ALL
            SELECT 'RLS policy', c.nspname, format('%I.%I', c.relname, p.polname),
                concat_ws(E'\n', 'Command: ' || p.polcmd::text, 'Permissive: ' || p.polpermissive::text,
                    'Roles: ' || COALESCE((SELECT string_agg(CASE WHEN role_oid = 0 THEN 'PUBLIC' ELSE quote_ident(pg_catalog.pg_get_userbyid(role_oid)) END, ', '
                        ORDER BY CASE WHEN role_oid = 0 THEN 'PUBLIC' ELSE quote_ident(pg_catalog.pg_get_userbyid(role_oid)) END COLLATE "C")
                        FROM unnest(p.polroles) role_oid), ''),
                    'Using: ' || COALESCE(pg_catalog.pg_get_expr(p.polqual, p.polrelid, false), ''),
                    'With check: ' || COALESCE(pg_catalog.pg_get_expr(p.polwithcheck, p.polrelid, false), ''),
                    'Comment: ' || COALESCE(pg_catalog.obj_description(p.oid, 'pg_policy'), ''))
            FROM user_relations c JOIN pg_catalog.pg_policy p ON p.polrelid = c.oid
            """),
        Bounded("""
            SELECT 'Grants' AS kind, grant_objects.schema_name, grant_objects.object_name,
                COALESCE((SELECT string_agg(format('%s -> %s: %s%s', quote_ident(pg_catalog.pg_get_userbyid(a.grantor)),
                    CASE WHEN a.grantee = 0 THEN 'PUBLIC' ELSE quote_ident(pg_catalog.pg_get_userbyid(a.grantee)) END,
                    a.privilege_type, CASE WHEN a.is_grantable THEN ' WITH GRANT OPTION' ELSE '' END), E'\n'
                    ORDER BY pg_catalog.pg_get_userbyid(a.grantor) COLLATE "C",
                        CASE WHEN a.grantee = 0 THEN 'PUBLIC' ELSE pg_catalog.pg_get_userbyid(a.grantee) END COLLATE "C",
                        a.privilege_type COLLATE "C", a.is_grantable)
                    FROM pg_catalog.aclexplode(grant_objects.acl) a), '(no direct grants)') AS definition
            FROM (
                SELECT n.nspname AS schema_name, 'Schema ' || quote_ident(n.nspname) AS object_name,
                    COALESCE(n.nspacl, pg_catalog.acldefault('n', n.nspowner)) AS acl FROM user_namespaces n
                WHERE NOT EXISTS (SELECT 1 FROM pg_catalog.pg_depend d WHERE d.classid = 'pg_catalog.pg_namespace'::regclass
                    AND d.objid = n.oid AND d.deptype = 'e')
                UNION ALL
                SELECT c.nspname, CASE WHEN c.relkind = 'S' THEN 'Sequence ' ELSE 'Relation ' END || quote_ident(c.relname),
                    COALESCE(c.relacl, pg_catalog.acldefault(CASE WHEN c.relkind = 'S' THEN 's'::"char" ELSE 'r'::"char" END, c.relowner))
                FROM user_relations c WHERE c.relkind <> 'c'
                UNION ALL
                SELECT c.nspname, 'Column ' || format('%I.%I', c.relname, a.attname), a.attacl
                FROM user_relations c JOIN pg_catalog.pg_attribute a ON a.attrelid = c.oid
                WHERE a.attnum > 0 AND NOT a.attisdropped AND a.attacl IS NOT NULL
                UNION ALL
                SELECT p.nspname, 'Routine ' || quote_ident(p.proname) || '(' || pg_catalog.oidvectortypes(p.proargtypes) || ')',
                    COALESCE(p.proacl, pg_catalog.acldefault('f', p.proowner)) FROM user_routines p
                UNION ALL
                SELECT t.nspname, 'Type ' || quote_ident(t.typname), COALESCE(t.typacl, pg_catalog.acldefault('T', t.typowner)) FROM user_types t
                UNION ALL
                SELECT ''::text, 'Database', COALESCE(d.datacl, pg_catalog.acldefault('d', d.datdba))
                FROM pg_catalog.pg_database d WHERE d.datname = current_database()
            ) grant_objects
            UNION ALL
            SELECT 'Default privileges', COALESCE(n.nspname, ''),
                quote_ident(pg_catalog.pg_get_userbyid(d.defaclrole)) || ' / ' || d.defaclobjtype::text,
                COALESCE((SELECT string_agg(format('%s -> %s: %s%s', quote_ident(pg_catalog.pg_get_userbyid(a.grantor)),
                    CASE WHEN a.grantee = 0 THEN 'PUBLIC' ELSE quote_ident(pg_catalog.pg_get_userbyid(a.grantee)) END,
                    a.privilege_type, CASE WHEN a.is_grantable THEN ' WITH GRANT OPTION' ELSE '' END), E'\n'
                    ORDER BY pg_catalog.pg_get_userbyid(a.grantor) COLLATE "C",
                        CASE WHEN a.grantee = 0 THEN 'PUBLIC' ELSE pg_catalog.pg_get_userbyid(a.grantee) END COLLATE "C",
                        a.privilege_type COLLATE "C", a.is_grantable)
                    FROM pg_catalog.aclexplode(d.defaclacl) a), '(no default grants)')
            FROM pg_catalog.pg_default_acl d LEFT JOIN user_namespaces n ON n.oid = d.defaclnamespace
            WHERE d.defaclnamespace = 0 OR n.oid IS NOT NULL
            """)
    ];
}
