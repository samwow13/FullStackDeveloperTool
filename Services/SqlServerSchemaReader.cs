using System.Data;
using System.Globalization;
using FullStackLauncher.Models;
using Microsoft.Data.SqlClient;

namespace FullStackLauncher.Services;

/// <summary>
/// Short-lived SQL Server 2016+ catalog reads and bounded previews. All issued SQL is generated SELECT SQL.
/// SQL Server has no PostgreSQL-style read-only transaction mode; a read-only login can enforce that policy server-side.
/// </summary>
public static class SqlServerSchemaReader
{
    private const int CommandTimeoutSeconds = 30;
    private const string VisibilityNotice = "SQL Server shows only databases and schema metadata visible to this login. Missing objects or definitions may require additional permissions.";

    public static async Task<DatabaseCatalog> ReadDatabasesAsync(DatabaseConnectionSource source, CancellationToken token)
    {
        using var timeout = CreateTimeout(token);
        token = timeout.Token;
        await using var connection = await OpenAsync(source, null, token);
        var names = new List<string>();
        try
        {
            await using var command = Command("""
                SELECT name FROM sys.databases
                WHERE state = 0 AND HAS_DBACCESS(name) = 1
                ORDER BY name
                """, connection);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) names.Add(reader.GetString(0));
        }
        catch (SqlException ex) when (ex.Number is 229 or 230 or 297 or 916)
        {
            return new(connection.Database, [connection.Database],
                "This login cannot list databases. You can still browse the configured database. " + VisibilityNotice);
        }
        if (!names.Contains(connection.Database, StringComparer.Ordinal)) names.Insert(0, connection.Database);
        return new(connection.Database, names, VisibilityNotice);
    }

    public static async Task<IReadOnlyList<DatabaseTable>> ReadTablesAsync(DatabaseConnectionSource source, string database,
        CancellationToken token)
    {
        using var timeout = CreateTimeout(token);
        token = timeout.Token;
        await using var connection = await OpenAsync(source, database, token);
        await using var command = Command("""
            SELECT o.object_id, s.name, o.name, CASE WHEN o.type = 'V' THEN 'View' ELSE 'Table' END
            FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.type IN ('U', 'V') AND o.is_ms_shipped = 0
            ORDER BY s.name, o.name
            """, connection);
        var tables = new List<DatabaseTable>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            tables.Add(new(checked((uint)reader.GetInt32(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return tables;
    }

    public static async Task<IReadOnlyList<DatabaseColumn>> ReadColumnsAsync(DatabaseConnectionSource source, string database,
        DatabaseTable table, CancellationToken token)
    {
        using var timeout = CreateTimeout(token);
        token = timeout.Token;
        await using var connection = await OpenAsync(source, database, token);
        await using var command = Command("""
            SELECT c.column_id, c.name, ts.name, t.name, t.is_user_defined, c.max_length, c.precision, c.scale,
                c.is_nullable, c.default_object_id, OBJECT_DEFINITION(c.default_object_id),
                c.is_identity, CONVERT(nvarchar(128), ic.seed_value), CONVERT(nvarchar(128), ic.increment_value),
                c.is_computed, cc.definition, cc.is_persisted, c.generated_always_type_desc, c.is_hidden,
                CONVERT(nvarchar(max), ep.value), c.encryption_type_desc
            FROM sys.columns c
            JOIN sys.objects o ON o.object_id = c.object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            JOIN sys.types t ON t.user_type_id = c.user_type_id
            JOIN sys.schemas ts ON ts.schema_id = t.schema_id
            LEFT JOIN sys.identity_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
            LEFT JOIN sys.extended_properties ep ON ep.class = 1 AND ep.major_id = c.object_id
                AND ep.minor_id = c.column_id AND ep.name = N'MS_Description'
            WHERE o.object_id = @objectId AND s.name = @schema AND o.name = @table
                AND o.type IN ('U', 'V') AND o.is_ms_shipped = 0
            ORDER BY c.column_id
            """, connection, table);
        var columns = new List<DatabaseColumn>();
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                var generation = new List<string>();
                if (reader.GetBoolean(11))
                    generation.Add($"Identity ({Text(reader, 12, "seed unavailable")}, {Text(reader, 13, "increment unavailable")})");
                if (reader.GetBoolean(14))
                    generation.Add($"Computed{(!reader.IsDBNull(16) && reader.GetBoolean(16) ? " persisted" : "")}: {Text(reader, 15, "definition unavailable")}");
                if (!reader.IsDBNull(17) && reader.GetString(17) != "NOT_APPLICABLE")
                    generation.Add($"Generated: {reader.GetString(17).Replace('_', ' ')}");
                if (reader.GetBoolean(18)) generation.Add("Hidden column");
                if (!reader.IsDBNull(20)) generation.Add($"Encrypted: {reader.GetString(20)}");
                columns.Add(new(reader.GetInt32(0), reader.GetString(1),
                    DescribeType(reader.GetString(2), reader.GetString(3), reader.GetBoolean(4),
                        reader.GetInt16(5), reader.GetByte(6), reader.GetByte(7)),
                    reader.GetBoolean(8) ? "Yes" : "No",
                    Text(reader, 10, reader.GetInt32(9) == 0 ? "" : "Definition unavailable with current permissions"),
                    string.Join(" · ", generation), "", Text(reader, 19)));
            }
        }
        var constraints = await ReadColumnConstraintsAsync(connection, table, token);
        return columns.Select(column => constraints.TryGetValue(column.Position, out var descriptions)
            ? column with { Constraints = string.Join("\n", descriptions) } : column).ToArray();
    }

    public static async Task<DatabaseRowPreview> ReadTopRowsAsync(DatabaseConnectionSource source, string database,
        DatabaseTable table, CancellationToken token)
    {
        using var timeout = CreateTimeout(token);
        token = timeout.Token;
        await using var connection = await OpenAsync(source, database, token);
        // This transaction contains only SELECTs. It is not a server-enforced read-only transaction.
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        var columns = new List<PreviewColumn>();
        await using (var metadata = Command("""
            SELECT c.name, c.system_type_id, t.name, ts.name, t.is_assembly_type, c.encryption_type,
                (SELECT ic.key_ordinal FROM sys.indexes i
                    JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                    WHERE i.object_id = c.object_id AND i.is_primary_key = 1 AND i.is_disabled = 0
                        AND ic.column_id = c.column_id AND ic.key_ordinal > 0), c.is_hidden
            FROM sys.columns c
            JOIN sys.objects o ON o.object_id = c.object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            JOIN sys.types t ON t.user_type_id = c.user_type_id
            JOIN sys.schemas ts ON ts.schema_id = t.schema_id
            WHERE o.object_id = @objectId AND s.name = @schema AND o.name = @table
                AND o.type IN ('U', 'V') AND o.is_ms_shipped = 0
            ORDER BY c.column_id
            """, connection, table, transaction))
        {
            await using var reader = await metadata.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                columns.Add(new(reader.GetString(0), reader.GetByte(1), reader.GetString(2), reader.GetString(3),
                    reader.GetBoolean(4), !reader.IsDBNull(5), reader.IsDBNull(6) ? 0 : reader.GetByte(6), reader.GetBoolean(7)));
        }
        var visibleColumns = columns.Where(column => !column.IsHidden).ToArray();
        if (visibleColumns.Length == 0)
            throw new InvalidOperationException("The selected table has no available columns. Refresh the schema.");
        var keys = columns.Where(column => column.KeyPosition > 0).OrderBy(column => column.KeyPosition).ToArray();
        // Never sort ciphertext; an encrypted key cannot provide the user's expected primary-key ordering.
        var canOrder = keys.Length > 0 && keys.All(column => !column.IsEncrypted);
        var order = canOrder ? " ORDER BY " + string.Join(", ", keys.Select(column => Quote(column.Name))) : "";
        var projection = string.Join(", ", visibleColumns.Select(PreviewExpression));
        var target = $"{Quote(table.Schema)}.{Quote(table.Name)}";
        await using var command = Command($"SELECT TOP (10) {projection} FROM {target} WHERE OBJECT_ID(@qualifiedName) = @objectId{order}",
            connection, table, transaction);
        command.Parameters.Add("@qualifiedName", SqlDbType.NVarChar, 517).Value = target;
        var rows = new List<string?[]>();
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                var row = new string?[visibleColumns.Length];
                for (var index = 0; index < row.Length; index++)
                {
                    if (reader.IsDBNull(index)) continue;
                    var value = reader.GetString(index);
                    row[index] = value.Length > 4096 ? value[..4096] + "… [truncated]" : value;
                }
                rows.Add(row);
            }
        }
        return new(visibleColumns.Select(column => column.Name).ToArray(), rows,
            canOrder ? "Ordered by primary key" : keys.Length > 0
                ? "Unordered sample · encrypted primary key" : "Unordered sample · no visible primary key");
    }

    public static async Task<IReadOnlyList<DatabaseRelationship>> ReadRelationshipsAsync(DatabaseConnectionSource source,
        string database, DatabaseTable table, CancellationToken token)
    {
        using var timeout = CreateTimeout(token);
        token = timeout.Token;
        await using var connection = await OpenAsync(source, database, token);
        await using var command = Command("""
            SELECT fk.object_id, fk.name,
                src.object_id, ss.name, src.name, dst.object_id, ds.name, dst.name,
                sc.name, dc.name, sc.is_nullable,
                fk.update_referential_action, fk.delete_referential_action,
                fk.is_not_trusted, fk.is_disabled, fk.is_not_for_replication,
                CONVERT(bit, CASE WHEN EXISTS (
                    SELECT 1 FROM sys.indexes i
                    WHERE i.object_id = src.object_id AND i.is_unique = 1 AND i.is_disabled = 0
                        AND i.is_hypothetical = 0 AND i.has_filter = 0
                        AND EXISTS (SELECT 1 FROM sys.index_columns ic WHERE ic.object_id = i.object_id
                            AND ic.index_id = i.index_id AND ic.key_ordinal > 0)
                        AND NOT EXISTS (SELECT 1 FROM sys.index_columns ic
                            WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0
                                AND NOT EXISTS (SELECT 1 FROM sys.foreign_key_columns fkc
                                    WHERE fkc.constraint_object_id = fk.object_id AND fkc.parent_column_id = ic.column_id))
                ) THEN 1 ELSE 0 END)
            FROM sys.foreign_keys fk
            JOIN sys.tables src ON src.object_id = fk.parent_object_id AND src.is_ms_shipped = 0
            JOIN sys.schemas ss ON ss.schema_id = src.schema_id
            JOIN sys.tables dst ON dst.object_id = fk.referenced_object_id AND dst.is_ms_shipped = 0
            JOIN sys.schemas ds ON ds.schema_id = dst.schema_id
            JOIN sys.foreign_key_columns pairs ON pairs.constraint_object_id = fk.object_id
            JOIN sys.columns sc ON sc.object_id = src.object_id AND sc.column_id = pairs.parent_column_id
            JOIN sys.columns dc ON dc.object_id = dst.object_id AND dc.column_id = pairs.referenced_column_id
            WHERE (src.object_id = @objectId AND ss.name = @schema AND src.name = @table)
                OR (dst.object_id = @objectId AND ds.name = @schema AND dst.name = @table)
            ORDER BY ss.name, src.name, ds.name, dst.name, fk.name, fk.object_id, pairs.constraint_column_id
            """, connection, table);
        var builders = new List<RelationshipBuilder>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var id = checked((uint)reader.GetInt32(0));
            var builder = builders.LastOrDefault();
            if (builder is null || builder.Id != id)
            {
                builder = new(id, reader.GetString(1),
                    new(checked((uint)reader.GetInt32(2)), reader.GetString(3), reader.GetString(4), "Table"),
                    new(checked((uint)reader.GetInt32(5)), reader.GetString(6), reader.GetString(7), "Table"),
                    reader.GetByte(11), reader.GetByte(12), !reader.GetBoolean(13), !reader.GetBoolean(14),
                    reader.GetBoolean(15), reader.GetBoolean(16));
                builders.Add(builder);
            }
            builder.Pairs.Add(new(reader.GetString(8), reader.GetString(9)));
            builder.IsOptional |= reader.GetBoolean(10);
        }
        return builders.Select(builder => builder.Build()).ToArray();
    }

    private static async Task<Dictionary<int, List<string>>> ReadColumnConstraintsAsync(SqlConnection connection,
        DatabaseTable table, CancellationToken token)
    {
        await using var command = Command("""
            SELECT ic.column_id, QUOTENAME(k.name) + N': ' +
                CASE WHEN k.type = 'PK' THEN N'PRIMARY KEY' ELSE N'UNIQUE' END +
                N' (key position ' + CONVERT(nvarchar(10), ic.key_ordinal) + N')' +
                CASE WHEN i.is_disabled = 1 THEN N' · disabled' ELSE N'' END
            FROM sys.key_constraints k
            JOIN sys.indexes i ON i.object_id = k.parent_object_id AND i.index_id = k.unique_index_id
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0
            JOIN sys.objects o ON o.object_id = k.parent_object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.object_id = @objectId AND s.name = @schema AND o.name = @table
            UNION ALL
            SELECT pairs.parent_column_id, QUOTENAME(fk.name) + N': FOREIGN KEY (position ' +
                CONVERT(nvarchar(10), pairs.constraint_column_id) + N') REFERENCES ' +
                QUOTENAME(ds.name) + N'.' + QUOTENAME(dst.name) + N' (' + QUOTENAME(dc.name) + N')' +
                CASE WHEN fk.is_disabled = 1 THEN N' · disabled' ELSE N'' END +
                CASE WHEN fk.is_not_trusted = 1 THEN N' · not trusted' ELSE N'' END
            FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns pairs ON pairs.constraint_object_id = fk.object_id
            JOIN sys.objects o ON o.object_id = fk.parent_object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            JOIN sys.tables dst ON dst.object_id = fk.referenced_object_id
            JOIN sys.schemas ds ON ds.schema_id = dst.schema_id
            JOIN sys.columns dc ON dc.object_id = dst.object_id AND dc.column_id = pairs.referenced_column_id
            WHERE o.object_id = @objectId AND s.name = @schema AND o.name = @table
            UNION ALL
            SELECT c.column_id, QUOTENAME(ch.name) + N': CHECK ' +
                COALESCE(ch.definition, N'[definition unavailable with current permissions]') +
                CASE WHEN ch.is_disabled = 1 THEN N' · disabled' ELSE N'' END +
                CASE WHEN ch.is_not_trusted = 1 THEN N' · not trusted' ELSE N'' END +
                CASE WHEN ch.parent_column_id = 0 THEN N' · table constraint' ELSE N'' END
            FROM sys.check_constraints ch
            JOIN sys.objects o ON o.object_id = ch.parent_object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            JOIN sys.columns c ON c.object_id = o.object_id
                AND (c.column_id = ch.parent_column_id OR ch.parent_column_id = 0)
            WHERE o.object_id = @objectId AND s.name = @schema AND o.name = @table
            ORDER BY 1, 2
            """, connection, table);
        var constraints = new Dictionary<int, List<string>>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var position = reader.GetInt32(0);
            if (!constraints.TryGetValue(position, out var entries)) constraints[position] = entries = [];
            entries.Add(reader.GetString(1));
        }
        return constraints;
    }

    private static string PreviewExpression(PreviewColumn column)
    {
        var name = Quote(column.Name);
        if (column.IsEncrypted)
            return $"CASE WHEN {name} IS NULL THEN NULL ELSE N'[encrypted value not previewed]' END";
        string expression;
        if (column.IsAssemblyType)
        {
            if (column.TypeSchema == "sys" && column.TypeName is "geometry" or "geography" or "hierarchyid")
                expression = $"{name}.ToString()";
            else return $"CASE WHEN {name} IS NULL THEN NULL ELSE N'[preview unavailable for this data type]' END";
        }
        else
        {
            expression = column.SystemType switch
            {
                34 or 165 or 173 or 189 => $"CONVERT(varchar(max), CONVERT(varbinary(max), {name}), 1)",
                59 or 62 => $"CONVERT(nvarchar(max), {name}, 3)",
                60 or 122 => $"CONVERT(nvarchar(max), {name}, 2)",
                40 or 41 or 42 or 43 or 58 or 61 => $"CONVERT(nvarchar(max), {name}, 126)",
                35 or 36 or 48 or 52 or 56 or 98 or 99 or 104 or 106 or 108 or 127 or 167 or 175 or 231 or 239 or 241
                    => $"CONVERT(nvarchar(max), {name})",
                _ => $"CASE WHEN {name} IS NULL THEN NULL ELSE N'[preview unavailable for this data type]' END"
            };
        }
        // Bound the SQL result before it reaches the client, including text, XML, binary and spatial values.
        return $"LEFT({expression}, 4097)";
    }

    private static string DescribeType(string schema, string name, bool userDefined, short length, byte precision, byte scale)
    {
        if (userDefined) return $"{Quote(schema)}.{Quote(name)}";
        return name switch
        {
            "varchar" or "char" or "varbinary" or "binary" => $"{name}({(length < 0 ? "max" : length.ToString(CultureInfo.InvariantCulture))})",
            "nvarchar" or "nchar" => $"{name}({(length < 0 ? "max" : (length / 2).ToString(CultureInfo.InvariantCulture))})",
            "decimal" or "numeric" => $"{name}({precision}, {scale})",
            "datetime2" or "datetimeoffset" or "time" => $"{name}({scale})",
            "float" => $"{name}({precision})",
            _ => name
        };
    }

    private static string Text(SqlDataReader reader, int ordinal, string fallback = "") =>
        reader.IsDBNull(ordinal) ? fallback : reader.GetString(ordinal);

    private static string Quote(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private static string DescribeAction(byte action) => action switch
    {
        0 => "No action", 1 => "Cascade", 2 => "Set null", 3 => "Set default", _ => "Unknown"
    };

    private static CancellationTokenSource CreateTimeout(CancellationToken token)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        return timeout;
    }

    private static SqlCommand Command(string sql, SqlConnection connection, DatabaseTable? table = null,
        SqlTransaction? transaction = null)
    {
        var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = CommandTimeoutSeconds };
        if (table is not null)
        {
            command.Parameters.Add("@objectId", SqlDbType.Int).Value = checked((int)table.Id);
            command.Parameters.Add("@schema", SqlDbType.NVarChar, 128).Value = table.Schema;
            command.Parameters.Add("@table", SqlDbType.NVarChar, 128).Value = table.Name;
        }
        return command;
    }

    private static async Task<SqlConnection> OpenAsync(DatabaseConnectionSource source, string? database, CancellationToken token)
    {
        var connection = source.CreateSqlServerConnection(database);
        try { await connection.OpenAsync(token); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    public static string DescribeError(Exception exception) => exception switch
    {
        SqlException { Number: 18456 or 18452 } => "SQL Server sign-in failed. Check the saved authentication method and credentials, then refresh connections.",
        SqlException { Number: 229 or 230 or 297 or 916 } => "This SQL Server login does not have permission to read the selected database, table or metadata.",
        SqlException { Number: 4060 or 911 } => "SQL Server could not open the selected database. Check its name and this login's database access, then refresh.",
        SqlException { Number: -2 or 1222 } or TimeoutException or OperationCanceledException => "The database request timed out. Check the server, firewall and network, then refresh.",
        SqlException { Number: 208 } => "The SQL Server object is no longer available or is hidden by permissions. Refresh the schema.",
        SqlException { Number: 207 } => "SQL Server could not read the requested columns. Refresh the schema. The explorer requires SQL Server 2016 or later.",
        SqlException ex => $"SQL Server could not load the requested information (error {ex.Number}). Check the server, permissions, credentials and verified TLS settings.",
        _ => $"Could not read the SQL Server configuration or schema ({exception.GetType().Name}). Check the connection source, then refresh."
    };

    private sealed record PreviewColumn(string Name, byte SystemType, string TypeName, string TypeSchema,
        bool IsAssemblyType, bool IsEncrypted, int KeyPosition, bool IsHidden);

    private sealed class RelationshipBuilder(uint id, string name, DatabaseTable source, DatabaseTable target,
        byte update, byte delete, bool validated, bool enforced, bool notForReplication, bool unique)
    {
        public uint Id { get; } = id;
        public List<DatabaseColumnPair> Pairs { get; } = [];
        public bool IsOptional { get; set; }

        public DatabaseRelationship Build()
        {
            var definition = $"CONSTRAINT {Quote(name)} FOREIGN KEY ({string.Join(", ", Pairs.Select(pair => Quote(pair.SourceColumn)))}) " +
                $"REFERENCES {Quote(target.Schema)}.{Quote(target.Name)} ({string.Join(", ", Pairs.Select(pair => Quote(pair.TargetColumn)))}) " +
                $"ON UPDATE {DescribeAction(update).ToUpperInvariant()} ON DELETE {DescribeAction(delete).ToUpperInvariant()}" +
                (notForReplication ? " NOT FOR REPLICATION" : "") +
                (!enforced ? "\nDisabled: the constraint is not enforced." : "") +
                (!validated ? "\nNot trusted: existing rows have not been verified by SQL Server." : "");
            return new(Id, name, source, target, Pairs.ToArray(), DescribeAction(update), DescribeAction(delete),
                "Simple", IsOptional, unique, validated, false, false, definition) { IsEnforced = enforced };
        }
    }
}
