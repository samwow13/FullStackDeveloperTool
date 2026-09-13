using FullStackLauncher.Models;
using Npgsql;
using NpgsqlTypes;

namespace FullStackLauncher.Services;

/// <summary>Short-lived, read-only catalog queries and bounded table previews. Never executes user SQL.</summary>
public static class PostgresSchemaReader
{
    public static async Task<DatabaseCatalog> ReadDatabasesAsync(DatabaseConnectionSource source, CancellationToken token)
    {
        await using var connection = await OpenAsync(source, null, token);
        var names = new List<string>();
        try
        {
            await using var command = new NpgsqlCommand("""
                SELECT datname FROM pg_catalog.pg_database
                WHERE datallowconn AND NOT datistemplate
                  AND pg_catalog.has_database_privilege(oid, 'CONNECT')
                ORDER BY datname
                """, connection);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) names.Add(reader.GetString(0));
        }
        catch (PostgresException ex) when (ex.SqlState == "42501")
        {
            return new(connection.Database, [connection.Database],
                "This role cannot list databases. You can still browse the configured database.");
        }
        if (!names.Contains(connection.Database, StringComparer.Ordinal)) names.Insert(0, connection.Database);
        return new(connection.Database, names, null);
    }

    public static async Task<IReadOnlyList<DatabaseTable>> ReadTablesAsync(DatabaseConnectionSource source, string database, CancellationToken token)
    {
        await using var connection = await OpenAsync(source, database, token);
        await using var command = new NpgsqlCommand("""
            SELECT c.oid, n.nspname, c.relname,
                CASE c.relkind WHEN 'v' THEN 'View' WHEN 'm' THEN 'Materialized view'
                    WHEN 'p' THEN 'Partitioned table' WHEN 'f' THEN 'Foreign table' ELSE 'Table' END
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('r', 'p', 'v', 'm', 'f')
              AND n.nspname <> 'information_schema' AND n.nspname !~ '^pg_'
              AND pg_catalog.has_schema_privilege(n.oid, 'USAGE')
              AND (pg_catalog.has_table_privilege(c.oid, 'SELECT, INSERT, UPDATE, DELETE, REFERENCES, TRIGGER')
                   OR pg_catalog.has_any_column_privilege(c.oid, 'SELECT, INSERT, UPDATE, REFERENCES'))
            ORDER BY n.nspname, c.relname
            """, connection);
        var tables = new List<DatabaseTable>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            tables.Add(new(reader.GetFieldValue<uint>(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return tables;
    }

    public static async Task<IReadOnlyList<DatabaseColumn>> ReadColumnsAsync(DatabaseConnectionSource source, string database,
        DatabaseTable table, CancellationToken token)
    {
        await using var connection = await OpenAsync(source, database, token);
        await using var command = new NpgsqlCommand("""
            SELECT a.attnum::integer, a.attname, pg_catalog.format_type(a.atttypid, a.atttypmod),
                CASE WHEN a.attnotnull OR t.typnotnull THEN 'No' ELSE 'Yes' END,
                COALESCE(pg_catalog.pg_get_expr(d.adbin, d.adrelid), ''),
                CASE a.attidentity WHEN 'a' THEN 'Identity: always' WHEN 'd' THEN 'Identity: by default'
                    ELSE CASE a.attgenerated WHEN 's' THEN 'Generated: stored' WHEN 'v' THEN 'Generated: virtual' ELSE '' END END,
                COALESCE((SELECT pg_catalog.string_agg(
                    pg_catalog.quote_ident(k.conname) || ': ' || pg_catalog.pg_get_constraintdef(k.oid, true), E'\n' ORDER BY k.conname)
                    FROM pg_catalog.pg_constraint k WHERE k.conrelid = a.attrelid
                    AND a.attnum = ANY(k.conkey)), ''),
                COALESCE(pg_catalog.col_description(a.attrelid, a.attnum), '')
            FROM pg_catalog.pg_attribute a
            JOIN pg_catalog.pg_class c ON c.oid = a.attrelid
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_catalog.pg_type t ON t.oid = a.atttypid
            LEFT JOIN pg_catalog.pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
            WHERE a.attrelid = $1 AND n.nspname = $2 AND c.relname = $3
              AND a.attnum > 0 AND NOT a.attisdropped
              AND pg_catalog.has_schema_privilege(n.oid, 'USAGE')
              AND (pg_catalog.has_table_privilege(c.oid, 'SELECT, INSERT, UPDATE, DELETE, REFERENCES, TRIGGER')
                   OR pg_catalog.has_column_privilege(c.oid, a.attnum, 'SELECT, INSERT, UPDATE, REFERENCES'))
            ORDER BY a.attnum
            """, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Oid, table.Id);
        command.Parameters.AddWithValue(table.Schema);
        command.Parameters.AddWithValue(table.Name);
        var columns = new List<DatabaseColumn>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            columns.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7)));
        return columns;
    }

    public static async Task<DatabaseRowPreview> ReadTopRowsAsync(DatabaseConnectionSource source, string database,
        DatabaseTable table, CancellationToken token)
    {
        await using var connection = await OpenAsync(source, database, token);
        var columns = new List<string>();
        var keys = new List<(string Name, int Position)>();
        // Re-resolve the selected catalog object before composing identifiers. Values cannot be SQL parameters here.
        await using (var metadata = new NpgsqlCommand("""
            SELECT a.attname, (SELECT pg_catalog.array_position(k.conkey, a.attnum)
                FROM pg_catalog.pg_constraint k WHERE k.conrelid = c.oid AND k.contype = 'p')
            FROM pg_catalog.pg_attribute a
            JOIN pg_catalog.pg_class c ON c.oid = a.attrelid
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE c.oid = $1 AND n.nspname = $2 AND c.relname = $3
                AND c.relkind IN ('r', 'p', 'v', 'm', 'f') AND a.attnum > 0 AND NOT a.attisdropped
            ORDER BY a.attnum
            """, connection))
        {
            metadata.Parameters.AddWithValue(NpgsqlDbType.Oid, table.Id);
            metadata.Parameters.AddWithValue(table.Schema);
            metadata.Parameters.AddWithValue(table.Name);
            await using var reader = await metadata.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                columns.Add(reader.GetString(0));
                if (!reader.IsDBNull(1)) keys.Add((reader.GetString(0), reader.GetInt32(1)));
            }
        }
        if (columns.Count == 0) throw new InvalidOperationException("The selected table has no available columns. Refresh the schema.");

        using var quoting = new NpgsqlCommandBuilder();
        var target = $"{quoting.QuoteIdentifier(table.Schema)}.{quoting.QuoteIdentifier(table.Name)}";
        var order = keys.Count == 0 ? "" : " ORDER BY " + string.Join(", ", keys.OrderBy(k => k.Position).Select(k => quoting.QuoteIdentifier(k.Name)));
        // Text projection supports extension types, arrays, JSON, binary and high-precision numerics without CLR mappings.
        // Bound every cell on the server so a ten-row preview cannot download unbounded text/blob fields.
        var projection = string.Join(", ", columns.Select(name => $"pg_catalog.left({quoting.QuoteIdentifier(name)}::text, 4097)"));
        await using var command = new NpgsqlCommand($"SELECT {projection} FROM {target}{order} LIMIT 10", connection);
        var rows = new List<string?[]>();
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                var row = new string?[columns.Count];
                for (var i = 0; i < row.Length; i++)
                {
                    if (reader.IsDBNull(i)) continue;
                    var value = reader.GetString(i);
                    row[i] = value.Length > 4096 ? value[..4096] + "… [truncated]" : value;
                }
                rows.Add(row);
            }
        }
        return new(columns, rows, keys.Count == 0 ? "Unordered sample · no primary key" : "Ordered by primary key");
    }

    public static async Task<IReadOnlyList<DatabaseRelationship>> ReadRelationshipsAsync(DatabaseConnectionSource source,
        string database, DatabaseTable table, CancellationToken token)
    {
        await using var connection = await OpenAsync(source, database, token);
        // Resolve both ends through the same visible-object rules as the table navigator.
        // Pair columns by their catalog ordinal so composite foreign keys stay together and ordered.
        await using var command = new NpgsqlCommand("""
            WITH visible_tables AS (
                SELECT c.oid, n.nspname, c.relname,
                    CASE c.relkind WHEN 'v' THEN 'View' WHEN 'm' THEN 'Materialized view'
                        WHEN 'p' THEN 'Partitioned table' WHEN 'f' THEN 'Foreign table' ELSE 'Table' END AS kind
                FROM pg_catalog.pg_class c
                JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                WHERE c.relkind IN ('r', 'p', 'v', 'm', 'f')
                  AND n.nspname <> 'information_schema' AND n.nspname !~ '^pg_'
                  AND pg_catalog.has_schema_privilege(n.oid, 'USAGE')
                  AND (pg_catalog.has_table_privilege(c.oid, 'SELECT, INSERT, UPDATE, DELETE, REFERENCES, TRIGGER')
                       OR pg_catalog.has_any_column_privilege(c.oid, 'SELECT, INSERT, UPDATE, REFERENCES'))
            )
            SELECT k.oid, k.conname,
                src.oid, src.nspname, src.relname, src.kind,
                dst.oid, dst.nspname, dst.relname, dst.kind,
                cols.source_columns, cols.target_columns,
                k.confupdtype::text, k.confdeltype::text, k.confmatchtype::text,
                CASE WHEN k.confmatchtype = 'f' THEN cols.all_nullable ELSE cols.any_nullable END,
                EXISTS (
                    SELECT 1 FROM pg_catalog.pg_index i
                    WHERE i.indrelid = k.conrelid AND i.indisunique AND i.indisvalid
                      AND i.indisready AND i.indislive AND i.indpred IS NULL AND i.indexprs IS NULL
                      AND i.indnkeyatts > 0
                      AND NOT EXISTS (
                          SELECT 1 FROM pg_catalog.unnest(i.indkey::smallint[]) WITH ORDINALITY AS indexed(attnum, position)
                          WHERE indexed.position <= i.indnkeyatts AND NOT (indexed.attnum = ANY(k.conkey))
                      )
                ),
                k.convalidated, k.condeferrable, k.condeferred,
                pg_catalog.pg_get_constraintdef(k.oid, true),
                COALESCE((pg_catalog.to_jsonb(k)->>'conperiod')::boolean, false),
                COALESCE((pg_catalog.to_jsonb(k)->>'conenforced')::boolean, true)
            FROM pg_catalog.pg_constraint k
            JOIN visible_tables src ON src.oid = k.conrelid
            JOIN visible_tables dst ON dst.oid = k.confrelid
            CROSS JOIN LATERAL (
                SELECT pg_catalog.array_agg(sa.attname::text ORDER BY pair.position) AS source_columns,
                    pg_catalog.array_agg(ta.attname::text ORDER BY pair.position) AS target_columns,
                    pg_catalog.bool_or(NOT (sa.attnotnull OR st.typnotnull)) AS any_nullable,
                    pg_catalog.bool_and(NOT (sa.attnotnull OR st.typnotnull)) AS all_nullable
                FROM pg_catalog.generate_subscripts(k.conkey, 1) AS pair(position)
                JOIN pg_catalog.pg_attribute sa ON sa.attrelid = k.conrelid AND sa.attnum = k.conkey[pair.position]
                JOIN pg_catalog.pg_attribute ta ON ta.attrelid = k.confrelid AND ta.attnum = k.confkey[pair.position]
                JOIN pg_catalog.pg_type st ON st.oid = sa.atttypid
                WHERE NOT sa.attisdropped AND NOT ta.attisdropped
            ) cols
            WHERE k.contype = 'f' AND cols.source_columns IS NOT NULL
              -- Referenced-partition clones implement the same FK; they are not extra declared links.
              AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_constraint parent
                  WHERE parent.oid = k.conparentid AND parent.conrelid = k.conrelid)
              AND ((src.oid = $1 AND src.nspname = $2 AND src.relname = $3)
                OR (dst.oid = $1 AND dst.nspname = $2 AND dst.relname = $3))
            ORDER BY src.nspname, src.relname, dst.nspname, dst.relname, k.conname, k.oid
            """, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Oid, table.Id);
        command.Parameters.AddWithValue(table.Schema);
        command.Parameters.AddWithValue(table.Name);
        var relationships = new List<DatabaseRelationship>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var sourceColumns = reader.GetFieldValue<string[]>(10);
            var targetColumns = reader.GetFieldValue<string[]>(11);
            var pairs = sourceColumns.Select((name, index) => new DatabaseColumnPair(name, targetColumns[index])).ToArray();
            relationships.Add(new(reader.GetFieldValue<uint>(0), reader.GetString(1),
                new(reader.GetFieldValue<uint>(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)),
                new(reader.GetFieldValue<uint>(6), reader.GetString(7), reader.GetString(8), reader.GetString(9)),
                pairs, DescribeReferentialAction(reader.GetString(12)), DescribeReferentialAction(reader.GetString(13)),
                reader.GetString(14) switch { "f" => "Full", "p" => "Partial", _ => "Simple" },
                reader.GetBoolean(15), reader.GetBoolean(16), reader.GetBoolean(17), reader.GetBoolean(18),
                reader.GetBoolean(19), reader.GetString(20))
            {
                IsTemporal = reader.GetBoolean(21),
                IsEnforced = reader.GetBoolean(22)
            });
        }
        return relationships;
    }

    private static string DescribeReferentialAction(string code) => code switch
    {
        "a" => "No action", "r" => "Restrict", "c" => "Cascade", "n" => "Set null", "d" => "Set default", _ => "Unknown"
    };

    private static async Task<NpgsqlConnection> OpenAsync(DatabaseConnectionSource source, string? database, CancellationToken token)
    {
        var connection = source.CreateConnection(database);
        try
        {
            await connection.OpenAsync(token);
            await using var command = new NpgsqlCommand("BEGIN READ ONLY", connection);
            await command.ExecuteNonQueryAsync(token);
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    public static string DescribeError(Exception exception) => exception switch
    {
        PostgresException { SqlState: "28P01" or "28000" } => "PostgreSQL sign-in failed. Update the credentials in the connection source, then refresh connections.",
        PostgresException { SqlState: "42501" } => "This PostgreSQL role does not have permission to read this database, table or its columns.",
        PostgresException { SqlState: "3D000" } => "The database no longer exists. Refresh connections and choose an existing database.",
        PostgresException ex => $"PostgreSQL could not load the requested information (SQLSTATE {ex.SqlState}). Check the database and role permissions, then refresh.",
        TimeoutException or OperationCanceledException => "The database request timed out. Check the server, firewall and network, then refresh.",
        NpgsqlException => "Could not connect to PostgreSQL. Check the server, firewall, credentials and TLS settings in the connection source.",
        _ => $"Could not read the database configuration or schema ({exception.GetType().Name}). Check the connection source, then refresh."
    };
}
