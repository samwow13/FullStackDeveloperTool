using FullStackLauncher.Models;
using Npgsql;

namespace FullStackLauncher.Services;

public static partial class DatabaseAccountService
{
    private static async Task<AccountSchema> ReadSchemaAsync(NpgsqlConnection connection, CancellationToken token)
    {
        var tables = new Dictionary<(string Schema, string Table), TableShape>();
        const string columnsSql = """
            SELECT n.nspname, c.relname, a.attname, t.typname, a.attnotnull,
                   a.atthasdef OR a.attidentity <> '' OR a.attgenerated <> '',
                   EXISTS (SELECT 1 FROM pg_catalog.pg_index i WHERE i.indrelid = c.oid
                           AND i.indisprimary AND a.attnum = ANY(i.indkey)),
                   EXISTS (SELECT 1 FROM pg_catalog.pg_index i WHERE i.indrelid = c.oid
                           AND i.indisunique AND i.indisvalid AND i.indpred IS NULL
                           AND i.indnkeyatts = 1 AND i.indkey[0] = a.attnum)
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_catalog.pg_attribute a ON a.attrelid = c.oid
            JOIN pg_catalog.pg_type t ON t.oid = a.atttypid
            WHERE c.relkind IN ('r', 'p') AND a.attnum > 0 AND NOT a.attisdropped
              AND n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname NOT LIKE 'pg_toast%'
            ORDER BY n.nspname, c.relname, a.attnum LIMIT 20001
            """;
        await using (var command = Command(connection, columnsSql))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            var count = 0;
            while (await reader.ReadAsync(token))
            {
                if (++count > 20000) throw new DatabaseAccountException("This database exceeds the account schema inspection limit.");
                var key = (reader.GetString(0), reader.GetString(1));
                if (!tables.TryGetValue(key, out var table)) tables[key] = table = new TableShape(key.Item1, key.Item2);
                var column = reader.GetString(2);
                table.Columns[column] = new(reader.GetString(3), reader.GetBoolean(4), reader.GetBoolean(5));
                if (reader.GetBoolean(6)) table.PrimaryKey.Add(column);
                if (reader.GetBoolean(7)) table.UniqueColumns.Add(column);
            }
        }
        const string foreignKeysSql = """
            SELECT sn.nspname, sc.relname,
                   ARRAY(SELECT a.attname::text FROM unnest(f.conkey) WITH ORDINALITY k(num, ord)
                         JOIN pg_catalog.pg_attribute a ON a.attrelid = sc.oid AND a.attnum = k.num ORDER BY k.ord),
                   tn.nspname, tc.relname,
                   ARRAY(SELECT a.attname::text FROM unnest(f.confkey) WITH ORDINALITY k(num, ord)
                         JOIN pg_catalog.pg_attribute a ON a.attrelid = tc.oid AND a.attnum = k.num ORDER BY k.ord),
                   f.confdeltype::text
            FROM pg_catalog.pg_constraint f
            JOIN pg_catalog.pg_class sc ON sc.oid = f.conrelid
            JOIN pg_catalog.pg_namespace sn ON sn.oid = sc.relnamespace
            JOIN pg_catalog.pg_class tc ON tc.oid = f.confrelid
            JOIN pg_catalog.pg_namespace tn ON tn.oid = tc.relnamespace
            WHERE f.contype = 'f' AND tn.nspname NOT IN ('pg_catalog', 'information_schema') LIMIT 10001
            """;
        var foreignKeys = new List<ForeignKeyShape>();
        await using (var command = Command(connection, foreignKeysSql))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                if (foreignKeys.Count == 10000) throw new DatabaseAccountException("This database exceeds the account relationship inspection limit.");
                foreignKeys.Add(new(reader.GetString(0), reader.GetString(1), reader.GetFieldValue<string[]>(2),
                    reader.GetString(3), reader.GetString(4), reader.GetFieldValue<string[]>(5), reader.GetString(6)));
            }
        }
        var candidates = new List<AccountSchema>();
        foreach (var group in tables.Values.GroupBy(table => table.Namespace))
        {
            var local = group.ToDictionary(table => table.Name, StringComparer.Ordinal);
            if (local.TryGetValue("Users", out var simUsers) && IsSimNow(local))
            {
                var schema = new AccountSchema(group.Key, "SimNow", local, foreignKeys);
                schema.CanCreate = simUsers.SupportsInsert("Id", "Username", "DisplayName", "PasswordHash", "IsActive",
                    "FailedLoginCount", "LockoutEndsAtUtc", "CreatedAtUtc", "LastLoginAtUtc")
                    && local["UserRoles"].SupportsInsert("UserId", "RoleId", "AssignedAtUtc")
                    && simUsers.UniqueColumns.Contains("Username") && local["Roles"].UniqueColumns.Contains("Name");
                schema.CanDelete = HasOnlyKnownUserReferences(schema, new Dictionary<(string, string), string>
                    { [("UserRoles", "UserId")] = "c" });
                candidates.Add(schema);
            }
            if (IsIdentity(local))
            {
                var crm = IsCrmAccountSchema(local);
                var schema = new AccountSchema(group.Key, crm ? "CRM" : "Identity", local, foreignKeys);
                schema.CanCreate = crm && local["AspNetUsers"].Type("PhoneNumber", "varchar", "text")
                    && local["AspNetUsers"].SupportsInsert("Id", "UserName", "NormalizedUserName", "Email",
                    "NormalizedEmail", "EmailConfirmed", "PasswordHash", "SecurityStamp", "ConcurrencyStamp", "PhoneNumber",
                    "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnd", "LockoutEnabled", "AccessFailedCount", "CreatedAtUtc", "ProjectId")
                    && local["AspNetRoles"].SupportsInsert("Id", "Name", "NormalizedName", "ConcurrencyStamp")
                    && local["AspNetUserRoles"].SupportsInsert("UserId", "RoleId")
                    && local["AspNetUsers"].UniqueColumns.Contains("NormalizedUserName")
                    && local["AspNetUsers"].UniqueColumns.Contains("NormalizedEmail")
                    && local["AspNetRoles"].UniqueColumns.Contains("NormalizedName");
                schema.CanDelete = crm && HasCrmRetentionSchema(schema);
                candidates.Add(schema);
            }
        }
        if (candidates.Count == 0)
            throw new DatabaseAccountException("No supported account table was found. Select the application's database. This tool recognizes SimNow Users and ASP.NET Identity AspNetUsers tables; other tables remain available in the Database browser.");
        if (candidates.Count > 1)
            throw new DatabaseAccountException("More than one account schema was found in this database. Account management requires one unambiguous account schema; inspect the tables in the Database browser.");
        return candidates[0];
    }

    private static bool IsSimNow(Dictionary<string, TableShape> tables) =>
        tables.TryGetValue("Users", out var users) && users.PrimaryKey.SetEquals(["Id"])
        && users.Type("Id", "uuid") && users.Type("Username", "varchar", "text")
        && users.Type("DisplayName", "varchar", "text") && users.Type("PasswordHash", "varchar", "text")
        && users.Type("IsActive", "bool") && users.Type("FailedLoginCount", "int4")
        && users.Type("LockoutEndsAtUtc", "timestamptz") && users.Type("CreatedAtUtc", "timestamptz")
        && users.Type("LastLoginAtUtc", "timestamptz")
        && tables.TryGetValue("Roles", out var roles) && roles.PrimaryKey.SetEquals(["Id"])
        && roles.Type("Id", "int4") && roles.Type("Name", "varchar", "text")
        && tables.TryGetValue("UserRoles", out var userRoles) && userRoles.PrimaryKey.SetEquals(["UserId", "RoleId"])
        && userRoles.Type("UserId", "uuid") && userRoles.Type("RoleId", "int4") && userRoles.Type("AssignedAtUtc", "timestamptz");

    private static bool IsIdentity(Dictionary<string, TableShape> tables) =>
        tables.TryGetValue("AspNetUsers", out var users) && users.PrimaryKey.SetEquals(["Id"])
        && users.Type("Id", "uuid", "text", "varchar", "int4", "int8") && users.Type("UserName", "varchar", "text")
        && users.Type("Email", "varchar", "text") && users.Type("PasswordHash", "varchar", "text")
        && users.Type("LockoutEnabled", "bool") && users.Type("LockoutEnd", "timestamptz")
        && users.Has("NormalizedUserName", "NormalizedEmail", "EmailConfirmed", "SecurityStamp", "ConcurrencyStamp",
            "PhoneNumberConfirmed", "TwoFactorEnabled", "AccessFailedCount")
        && tables.TryGetValue("AspNetRoles", out var roles) && roles.PrimaryKey.SetEquals(["Id"])
        && roles.Type("Id", "uuid", "text", "varchar", "int4", "int8")
        && roles.Type("Name", "varchar", "text") && roles.Type("NormalizedName", "varchar", "text")
        && roles.Has("ConcurrencyStamp")
        && tables.TryGetValue("AspNetUserRoles", out var userRoles) && userRoles.Has("UserId", "RoleId")
        && CompatibleIdentityKey(users.Columns["Id"].Type, userRoles.Columns["UserId"].Type)
        && CompatibleIdentityKey(roles.Columns["Id"].Type, userRoles.Columns["RoleId"].Type);

    private static bool CompatibleIdentityKey(string left, string right) => left == right
        || left is "varchar" or "text" && right is "varchar" or "text"
        || left is "int4" or "int8" && right is "int4" or "int8";

    // Only the known CRM credential/storage contract enables writes. Other Identity stores
    // remain visible even when they use string role IDs or application-specific field types.
    private static bool IsCrmAccountSchema(Dictionary<string, TableShape> tables)
    {
        var users = tables["AspNetUsers"];
        var roles = tables["AspNetRoles"];
        var userRoles = tables["AspNetUserRoles"];
        return users.Type("Id", "uuid") && users.Type("ProjectId", "uuid")
            && users.Type("CreatedAtUtc", "timestamptz")
            && users.Type("EmailConfirmed", "bool") && users.Type("PhoneNumberConfirmed", "bool")
            && users.Type("TwoFactorEnabled", "bool") && users.Type("AccessFailedCount", "int4")
            && new[] { "NormalizedUserName", "NormalizedEmail", "SecurityStamp", "ConcurrencyStamp" }
                .All(column => users.Type(column, "varchar", "text"))
            && roles.Type("Id", "uuid")
            && new[] { "Name", "NormalizedName", "ConcurrencyStamp" }.All(column => roles.Type(column, "varchar", "text"))
            && userRoles.PrimaryKey.SetEquals(["UserId", "RoleId"])
            && userRoles.Type("UserId", "uuid") && userRoles.Type("RoleId", "uuid")
            && tables.TryGetValue("Projects", out var projects) && projects.PrimaryKey.SetEquals(["Id"])
            && projects.Type("Id", "uuid") && projects.Type("Name", "varchar", "text")
            && projects.Type("Status", "varchar", "text") && projects.Type("UpdatedAtUtc", "timestamptz")
            && tables.TryGetValue("AccountAccessGrants", out var grants)
            && grants.PrimaryKey.SetEquals(["UserId", "Purpose"]) && grants.Type("UserId", "uuid")
            && new[] { "Purpose", "TokenHash", "SecurityStamp", "NormalizedEmail" }
                .All(column => grants.Type(column, "varchar", "text"));
    }

    private static bool HasOnlyKnownUserReferences(AccountSchema schema, Dictionary<(string Table, string Column), string> expected)
        => HasOnlyKnownReferences(schema, schema.UserTable, expected);

    private static bool HasOnlyKnownReferences(AccountSchema schema, string targetTable,
        Dictionary<(string Table, string Column), string> expected)
    {
        var actual = schema.ForeignKeys.Where(foreignKey => foreignKey.TargetSchema == schema.Namespace &&
            foreignKey.TargetTable == targetTable).ToArray();
        return actual.Length == expected.Count && actual.All(foreignKey => foreignKey.SourceSchema == schema.Namespace
            && foreignKey.SourceColumns.Length == 1 && foreignKey.TargetColumns.SequenceEqual(["Id"])
            && expected.TryGetValue((foreignKey.SourceTable, foreignKey.SourceColumns[0]), out var action)
            && action == foreignKey.DeleteAction)
            && actual.Select(foreignKey => (foreignKey.SourceTable, foreignKey.SourceColumns[0])).Distinct().Count() == expected.Count;
    }

    private static bool HasCrmRetentionSchema(AccountSchema schema)
    {
        var required = new Dictionary<string, string[]>
        {
            ["Projects"] = ["Id", "UpdatedAtUtc"],
            ["ProjectWorkItems"] = ["Id", "ProjectId", "CreatedByUserId", "CreatedByName"],
            ["WorkItemInformationRequests"] = ["WorkItemId", "RequestedByUserId", "RequestedByName", "RepliedByUserId", "RepliedByName"],
            ["WorkItemQuotes"] = ["Id", "WorkItemId", "CreatedByUserId", "CreatedByName", "RecipientUserId", "RecipientName", "RecipientEmail"],
            ["QuoteBillingAccounts"] = ["QuoteId", "CustomerId"],
            ["QuotePaymentReceipts"] = ["RecordedByUserId", "RecordedByName"],
            ["BillingPlans"] = ["Id", "ProjectId", "CustomerId", "Enabled", "RemindersEnabled", "CustomerName", "Revision", "UpdatedAtUtc",
                "StripeCheckoutAttemptId", "StripeCheckoutSessionId", "StripeCheckoutExpiresAtUtc", "StripeSubscriptionId",
                "StripeLastSyncedAtUtc", "StripeSubscriptionStatus"],
            ["BillingCharges"] = ["PlanId", "CustomerId", "StripeInvoiceId", "StripeInvoiceStatus"]
        };
        if (required.Any(table => !schema.Tables.TryGetValue(table.Key, out var shape) || !shape.Has(table.Value))) return false;
        return HasOnlyKnownUserReferences(schema, new Dictionary<(string, string), string>
        {
            [("AspNetUserClaims", "UserId")] = "c", [("AspNetUserLogins", "UserId")] = "c",
            [("AspNetUserRoles", "UserId")] = "c", [("AspNetUserTokens", "UserId")] = "c",
            [("AccountAccessGrants", "UserId")] = "c", [("ProjectWorkItems", "CreatedByUserId")] = "n",
            [("WorkItemInformationRequests", "RequestedByUserId")] = "n", [("WorkItemInformationRequests", "RepliedByUserId")] = "n",
            [("WorkItemQuotes", "CreatedByUserId")] = "n", [("WorkItemQuotes", "RecipientUserId")] = "n",
            [("QuoteBillingAccounts", "CustomerId")] = "n", [("QuotePaymentReceipts", "RecordedByUserId")] = "n",
            [("BillingPlans", "CustomerId")] = "n", [("BillingCharges", "CustomerId")] = "n"
        })
        // Unbilled work deletion cascades through this exact graph. An additional dependency
        // could contain application-specific history, so unknown graphs remain portal-only.
        && HasOnlyKnownReferences(schema, "ProjectWorkItems", new Dictionary<(string, string), string>
        {
            [("WorkItemInformationRequests", "WorkItemId")] = "c", [("WorkItemQuotes", "WorkItemId")] = "c"
        })
        && HasOnlyKnownReferences(schema, "WorkItemQuotes", new Dictionary<(string, string), string>
        {
            [("QuoteBillingAccounts", "QuoteId")] = "r"
        })
        && HasOnlyKnownReferences(schema, "WorkItemInformationRequests", []);
    }
}
