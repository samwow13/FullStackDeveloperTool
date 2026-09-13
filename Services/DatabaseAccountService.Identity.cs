using System.Buffers.Binary;
using System.Security.Cryptography;
using FullStackLauncher.Models;
using Npgsql;

namespace FullStackLauncher.Services;

public static partial class DatabaseAccountService
{
    private static async Task<DatabaseAccountResponse> ListIdentityAsync(NpgsqlConnection connection,
        AccountSchema schema, CancellationToken token)
    {
        var accounts = new List<DatabaseAccount>();
        var projects = new List<DatabaseAccountProject>();
        var blocked = new HashSet<string>(StringComparer.Ordinal);
        if (schema.CanDelete)
        {
            var blockedSql = $"""
                SELECT p."CustomerId"::text FROM {Table(schema, "BillingPlans")} p
                WHERE p."CustomerId" IS NOT NULL AND ({UnsafeStripePlan("p")})
                UNION SELECT c."CustomerId"::text FROM {Table(schema, "BillingCharges")} c
                WHERE c."CustomerId" IS NOT NULL AND ({UnresolvedStripeCharge("c")})
                UNION SELECT p."CustomerId"::text FROM {Table(schema, "BillingCharges")} c
                JOIN {Table(schema, "BillingPlans")} p ON p."Id" = c."PlanId"
                WHERE p."CustomerId" IS NOT NULL AND ({UnresolvedStripeCharge("c")})
                LIMIT {MaximumAccounts + 1}
                """;
            await using var command = Command(connection, blockedSql);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) blocked.Add(reader.GetString(0));
            if (blocked.Count > MaximumAccounts)
                throw new DatabaseAccountException("This database exceeds the launcher limit of 5,000 accounts.");
        }
        var projectColumn = schema.Kind == "CRM" ? "p.\"Name\"" : "NULL::text";
        var projectJoin = schema.Kind == "CRM"
            ? $"LEFT JOIN {Table(schema, "Projects")} p ON p.\"Id\" = u.\"ProjectId\"" : "";
        var sql = $"""
            SELECT u."Id"::text, u."UserName", u."Email", {projectColumn},
                   CASE WHEN u."LockoutEnabled" AND u."LockoutEnd" >= CURRENT_TIMESTAMP THEN 'Locked'
                        WHEN u."PasswordHash" IS NOT NULL THEN 'Active' ELSE 'PendingActivation' END,
                   ARRAY(SELECT COALESCE(r."Name", r."NormalizedName", '') FROM {Table(schema, "AspNetUserRoles")} ur
                         JOIN {Table(schema, "AspNetRoles")} r ON r."Id" = ur."RoleId"
                         WHERE ur."UserId" = u."Id" ORDER BY r."Name"),
                   EXISTS(SELECT 1 FROM {Table(schema, "AspNetUserRoles")} ur
                          JOIN {Table(schema, "AspNetRoles")} r ON r."Id" = ur."RoleId"
                          WHERE ur."UserId" = u."Id" AND (upper(r."Name") IN ('ADMINISTRATOR','ADMIN')
                                OR r."NormalizedName" IN ('ADMINISTRATOR','ADMIN')))
            FROM {Table(schema, "AspNetUsers")} u {projectJoin}
            ORDER BY u."UserName", u."Id" LIMIT {MaximumAccounts + 1}
            """;
        await using (var command = Command(connection, sql))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                if (accounts.Count == MaximumAccounts)
                    throw new DatabaseAccountException("This database exceeds the launcher limit of 5,000 accounts.");
                var id = reader.GetString(0);
                var roles = reader.GetFieldValue<string[]>(5);
                var administrator = reader.GetBoolean(6);
                accounts.Add(new DatabaseAccount
                {
                    Id = id, UserName = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    Email = reader.IsDBNull(2) ? null : reader.GetString(2),
                    ProjectName = reader.IsDBNull(3) ? null : reader.GetString(3), Status = reader.GetString(4),
                    Role = roles.Length > 0 ? string.Join(", ", roles) : schema.Kind == "CRM" ? "Customer" : "Unassigned",
                    CanDelete = schema.CanDelete && !administrator && !blocked.Contains(id)
                });
            }
        }
        if (schema.Kind == "CRM")
        {
            await using var command = Command(connection, $"""
                SELECT "Id"::text, "Name" FROM {Table(schema, "Projects")}
                WHERE "Status" = 'Active' ORDER BY "Name", "Id" LIMIT {MaximumProjects + 1}
                """);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                if (projects.Count == MaximumProjects)
                    throw new DatabaseAccountException("This database exceeds the launcher limit of 2,000 active projects.");
                projects.Add(new() { Id = reader.GetString(0), Name = reader.GetString(1) });
            }
        }
        return ListResponse(schema, accounts, projects, schema.Kind == "CRM" ? ["Customer", "Administrator"] : []);
    }

    private static async Task<string> MutateIdentityAsync(NpgsqlConnection connection, AccountSchema schema,
        DatabaseAccountRequest request, CancellationToken token)
    {
        if (request.Operation == "create") return await CreateIdentityAsync(connection, schema, request, token);
        await ConfirmAccountAsync(connection, schema, request, token);
        var id = Guid.Parse(request.UserId!);
        if (request.Operation == "set-password")
        {
            await ExecuteSqlAsync(connection, $"""
                UPDATE {Table(schema, "AspNetUsers")} SET "PasswordHash" = @hash,
                    "SecurityStamp" = @security, "ConcurrencyStamp" = @concurrency,
                    "AccessFailedCount" = 0, "LockoutEnd" = NULL, "LockoutEnabled" = TRUE WHERE "Id" = @id
                """, token, ("hash", HashIdentityPassword(request.Password!)), ("security", Guid.NewGuid().ToString("N")),
                ("concurrency", Guid.NewGuid().ToString()), ("id", id));
            await ExecuteSqlAsync(connection, $"DELETE FROM {Table(schema, "AccountAccessGrants")} WHERE \"UserId\" = @id", token, ("id", id));
            return "Password saved and account unlocked. Existing sessions and invitation/reset links are invalidated. No email was sent.";
        }
        return await DeleteIdentityAsync(connection, schema, id, token);
    }

    private static async Task<string> CreateIdentityAsync(NpgsqlConnection connection, AccountSchema schema,
        DatabaseAccountRequest request, CancellationToken token)
    {
        Guid? projectId = null;
        if (!string.IsNullOrEmpty(request.ProjectId))
        {
            if (!Guid.TryParse(request.ProjectId, out var value) || value == Guid.Empty)
                throw new DatabaseAccountException("Choose an active project for the account.");
            projectId = value;
            await using var project = Command(connection,
                $"SELECT \"Status\" FROM {Table(schema, "Projects")} WHERE \"Id\" = @id FOR UPDATE", ("id", value));
            if (!string.Equals(await project.ExecuteScalarAsync(token) as string, "Active", StringComparison.Ordinal))
                throw new DatabaseAccountException("The selected project is no longer active. Refresh and choose an active project.");
        }
        var userName = request.UserName!.Trim();
        var email = request.Email!.Trim();
        var normalizedName = NormalizeIdentity(userName);
        var normalizedEmail = NormalizeIdentity(email);
        if (await ExistsAsync(connection, $"""
            SELECT EXISTS(SELECT 1 FROM {Table(schema, "AspNetUsers")}
                WHERE "NormalizedUserName" = @name OR "NormalizedEmail" = @email)
            """, token, ("name", normalizedName), ("email", normalizedEmail)))
            throw new DatabaseAccountException("That username or email is already in use. Choose another username and email.");
        var id = Guid.NewGuid();
        await ExecuteSqlAsync(connection, $"""
            INSERT INTO {Table(schema, "AspNetUsers")}
                ("Id", "UserName", "NormalizedUserName", "Email", "NormalizedEmail", "EmailConfirmed", "PasswordHash",
                 "SecurityStamp", "ConcurrencyStamp", "PhoneNumber", "PhoneNumberConfirmed", "TwoFactorEnabled",
                 "LockoutEnd", "LockoutEnabled", "AccessFailedCount", "CreatedAtUtc", "ProjectId")
            VALUES (@id, @name, @normalizedName, @email, @normalizedEmail, FALSE, @hash, @security, @concurrency,
                    NULL, FALSE, FALSE, NULL, TRUE, 0, @now, @project::uuid)
            """, token, ("id", id), ("name", userName), ("normalizedName", normalizedName), ("email", email),
            ("normalizedEmail", normalizedEmail), ("hash", HashIdentityPassword(request.Password!)),
            ("security", Guid.NewGuid().ToString("N")), ("concurrency", Guid.NewGuid().ToString()),
            ("now", DateTime.UtcNow), ("project", projectId));
        if (request.Role == "Administrator")
        {
            await ExecuteSqlAsync(connection, $"""
                INSERT INTO {Table(schema, "AspNetRoles")} ("Id", "Name", "NormalizedName", "ConcurrencyStamp")
                VALUES (@id, 'Administrator', 'ADMINISTRATOR', @stamp)
                ON CONFLICT ("NormalizedName") DO NOTHING
                """, token, ("id", Guid.NewGuid()), ("stamp", Guid.NewGuid().ToString()));
            await using var role = Command(connection,
                $"SELECT \"Id\" FROM {Table(schema, "AspNetRoles")} WHERE \"NormalizedName\" = 'ADMINISTRATOR' FOR SHARE");
            var roleId = await role.ExecuteScalarAsync(token);
            if (roleId is not Guid)
                throw new DatabaseAccountException("The Administrator role is unavailable. Refresh accounts before trying again.");
            await ExecuteSqlAsync(connection,
                $"INSERT INTO {Table(schema, "AspNetUserRoles")} (\"UserId\", \"RoleId\") VALUES (@user, @role)",
                token, ("user", id), ("role", roleId));
        }
        return "Account created with a securely hashed password. No email was sent.";
    }

    private static async Task<string> DeleteIdentityAsync(NpgsqlConnection connection, AccountSchema schema,
        Guid id, CancellationToken token)
    {
        await using (var roles = Command(connection, $"""
            SELECT r."Name", r."NormalizedName" FROM {Table(schema, "AspNetUserRoles")} ur
            JOIN {Table(schema, "AspNetRoles")} r ON r."Id" = ur."RoleId" WHERE ur."UserId" = @id FOR SHARE OF r, ur
            """, ("id", id)))
        await using (var reader = await roles.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
                if ((!reader.IsDBNull(0) && IsAdministrator(reader.GetString(0))) ||
                    (!reader.IsDBNull(1) && IsAdministrator(reader.GetString(1))))
                    throw new DatabaseAccountException("Administrator accounts are permanent and cannot be deleted.");
        }

        // Match the portal's account-then-project lock order before checking billing or retaining work.
        var projectIds = new List<Guid>();
        await using (var command = Command(connection, $"""
            SELECT p."Id" FROM {Table(schema, "Projects")} p WHERE p."Id" IN (
                SELECT u."ProjectId" FROM {Table(schema, "AspNetUsers")} u WHERE u."Id" = @id
                UNION SELECT w."ProjectId" FROM {Table(schema, "ProjectWorkItems")} w WHERE w."CreatedByUserId" = @id
                UNION SELECT b."ProjectId" FROM {Table(schema, "BillingPlans")} b WHERE b."CustomerId" = @id
                UNION SELECT w."ProjectId" FROM {Table(schema, "WorkItemQuotes")} q
                    JOIN {Table(schema, "ProjectWorkItems")} w ON w."Id" = q."WorkItemId"
                    WHERE q."CreatedByUserId" = @id OR q."RecipientUserId" = @id
                UNION SELECT w."ProjectId" FROM {Table(schema, "WorkItemInformationRequests")} i
                    JOIN {Table(schema, "ProjectWorkItems")} w ON w."Id" = i."WorkItemId"
                    WHERE i."RequestedByUserId" = @id OR i."RepliedByUserId" = @id
                UNION SELECT b."ProjectId" FROM {Table(schema, "BillingCharges")} c
                    JOIN {Table(schema, "BillingPlans")} b ON b."Id" = c."PlanId" WHERE c."CustomerId" = @id
            ) ORDER BY p."Id" FOR UPDATE
            """, ("id", id)))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token)) projectIds.Add(reader.GetGuid(0));
        }
        if (await ExistsAsync(connection, $"""
            SELECT EXISTS(SELECT 1 FROM {Table(schema, "BillingPlans")} p
                          WHERE p."CustomerId" = @id AND ({UnsafeStripePlan("p")}))
                OR EXISTS(SELECT 1 FROM {Table(schema, "BillingCharges")} c
                          JOIN {Table(schema, "BillingPlans")} p ON p."Id" = c."PlanId"
                          WHERE (c."CustomerId" = @id OR p."CustomerId" = @id) AND ({UnresolvedStripeCharge("c")}))
            """, token, ("id", id)))
            throw new DatabaseAccountException("This customer has active, pending, or unresolved Stripe billing. Delete the customer through the administrator portal so Stripe cleanup can complete safely.");

        await ExecuteSqlAsync(connection, $"""
            UPDATE {Table(schema, "BillingPlans")} SET "Enabled" = FALSE, "RemindersEnabled" = FALSE,
                "CustomerName" = 'Deleted customer', "Revision" = @revision, "UpdatedAtUtc" = @now WHERE "CustomerId" = @id;
            DELETE FROM {Table(schema, "ProjectWorkItems")} w WHERE w."CreatedByUserId" = @id AND NOT EXISTS (
                SELECT 1 FROM {Table(schema, "QuoteBillingAccounts")} a
                JOIN {Table(schema, "WorkItemQuotes")} q ON q."Id" = a."QuoteId" WHERE q."WorkItemId" = w."Id");
            UPDATE {Table(schema, "ProjectWorkItems")} SET "CreatedByName" = 'Deleted customer' WHERE "CreatedByUserId" = @id;
            UPDATE {Table(schema, "WorkItemInformationRequests")} SET "RequestedByName" = 'Deleted customer' WHERE "RequestedByUserId" = @id;
            UPDATE {Table(schema, "WorkItemInformationRequests")} SET "RepliedByName" = 'Deleted customer' WHERE "RepliedByUserId" = @id;
            UPDATE {Table(schema, "WorkItemQuotes")} SET "CreatedByName" = 'Deleted customer' WHERE "CreatedByUserId" = @id;
            UPDATE {Table(schema, "WorkItemQuotes")} SET "RecipientName" = 'Deleted customer', "RecipientEmail" = '' WHERE "RecipientUserId" = @id;
            UPDATE {Table(schema, "QuotePaymentReceipts")} SET "RecordedByName" = 'Deleted customer' WHERE "RecordedByUserId" = @id;
            DELETE FROM {Table(schema, "AspNetUsers")} WHERE "Id" = @id;
            UPDATE {Table(schema, "Projects")} SET "UpdatedAtUtc" = @now WHERE "Id" = ANY(@projects)
            """, token, ("id", id), ("revision", Guid.NewGuid()), ("now", DateTime.UtcNow), ("projects", projectIds.ToArray()));
        return "Customer deleted. Unbilled work and account access were removed; billed work and payment history were retained. Local recurring billing is disabled.";
    }

    private static string UnsafeStripePlan(string alias) => $"""
        {alias}."StripeCheckoutAttemptId" IS NOT NULL OR {alias}."StripeCheckoutSessionId" IS NOT NULL
        OR {alias}."StripeCheckoutExpiresAtUtc" IS NOT NULL
        OR ({alias}."StripeSubscriptionId" IS NOT NULL AND ({alias}."StripeLastSyncedAtUtc" IS NULL
            OR {alias}."StripeSubscriptionStatus" IS NULL OR {alias}."StripeSubscriptionStatus" NOT IN ('canceled', 'incomplete_expired')))
        OR ({alias}."StripeSubscriptionStatus" IS NOT NULL AND {alias}."StripeSubscriptionStatus" NOT IN ('canceled', 'incomplete_expired'))
        """;

    private static string UnresolvedStripeCharge(string alias) => $"""
        {alias}."StripeInvoiceId" IS NOT NULL AND ({alias}."StripeInvoiceStatus" IS NULL
            OR {alias}."StripeInvoiceStatus" NOT IN ('paid', 'void', 'uncollectible'))
        """;

    private static string HashIdentityPassword(string password)
    {
        // ASP.NET Core Identity v3: marker, big-endian PRF/iterations/salt length, salt, subkey.
        // PRF 2 is HMAC-SHA512; the CRM's net8 PasswordHasher default uses 100,000 iterations.
        var salt = RandomNumberGenerator.GetBytes(16);
        var subkey = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA512, 32);
        var payload = new byte[13 + salt.Length + subkey.Length];
        try
        {
            payload[0] = 1;
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(1), 2);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(5), 100_000);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(9), (uint)salt.Length);
            salt.CopyTo(payload, 13);
            subkey.CopyTo(payload, 13 + salt.Length);
            return Convert.ToBase64String(payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(subkey);
            CryptographicOperations.ZeroMemory(payload);
        }
    }
}
