using System.Security.Cryptography;
using FullStackLauncher.Models;
using Npgsql;

namespace FullStackLauncher.Services;

public static partial class DatabaseAccountService
{
    private static async Task<DatabaseAccountResponse> ListSimNowAsync(
        NpgsqlConnection connection, AccountSchema schema, CancellationToken token)
    {
        var accounts = new List<DatabaseAccount>();
        await using (var command = Command(connection, $"""
            SELECT u."Id"::text, u."Username", u."DisplayName", u."IsActive", u."LockoutEndsAtUtc",
                   ARRAY(SELECT r."Name"::text FROM {Table(schema, "UserRoles")} ur
                         JOIN {Table(schema, "Roles")} r ON r."Id" = ur."RoleId"
                         WHERE ur."UserId" = u."Id" ORDER BY r."Name")
            FROM {Table(schema, "Users")} u
            ORDER BY u."Username", u."Id"
            LIMIT @limit
            """, ("limit", MaximumAccounts + 1)))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                if (accounts.Count == MaximumAccounts)
                    throw new DatabaseAccountException($"This database has more than {MaximumAccounts:N0} accounts. Use the application's account tools to browse this larger directory.");
                var roles = reader.GetFieldValue<string[]>(5);
                var isActive = reader.GetBoolean(3);
                var locked = !reader.IsDBNull(4) && reader.GetDateTime(4) > DateTime.UtcNow;
                accounts.Add(new DatabaseAccount
                {
                    Id = reader.GetString(0),
                    UserName = reader.GetString(1),
                    DisplayName = reader.GetString(2),
                    Role = roles.Length == 0 ? "No role" : string.Join(", ", roles),
                    Status = !isActive ? "Inactive" : locked ? "Locked" : "Active",
                    CanDelete = schema.CanDelete && !roles.Any(IsAdministrator)
                });
            }
        }

        var availableRoles = new List<string>();
        await using (var command = Command(connection, $"""
            SELECT "Name" FROM {Table(schema, "Roles")}
            WHERE "Name" IN ('Admin', 'Member') ORDER BY "Name"
            """))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token)) availableRoles.Add(reader.GetString(0));
        }
        schema.CanCreate &= availableRoles.Count > 0;
        return ListResponse(schema, accounts, [], availableRoles);
    }

    private static async Task<string> MutateSimNowAsync(NpgsqlConnection connection, AccountSchema schema,
        DatabaseAccountRequest request, CancellationToken token)
    {
        if (request.Operation == "create")
        {
            var username = request.UserName!.Trim().ToLowerInvariant();
            if (await ExistsAsync(connection, $"""
                SELECT EXISTS(SELECT 1 FROM {Table(schema, "Users")} WHERE lower("Username") = @username)
                """, token, ("username", username)))
                throw new DatabaseAccountException("That username is already in use. Choose a unique username.");

            int roleId;
            await using (var command = Command(connection, $"""
                SELECT "Id" FROM {Table(schema, "Roles")} WHERE "Name" = @role FOR SHARE
                """, ("role", request.Role)))
            {
                var value = await command.ExecuteScalarAsync(token);
                if (value is not int id)
                    throw new DatabaseAccountException("The selected role no longer exists. Refresh accounts and select an available role.");
                roleId = id;
            }

            var userId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            await ExecuteSqlAsync(connection, $"""
                INSERT INTO {Table(schema, "Users")}
                    ("Id", "Username", "DisplayName", "PasswordHash", "IsActive", "FailedLoginCount",
                     "LockoutEndsAtUtc", "CreatedAtUtc", "LastLoginAtUtc")
                VALUES (@id, @username, @displayName, @hash, TRUE, 0, NULL, @now, NULL)
                """, token, ("id", userId), ("username", username), ("displayName", request.DisplayName!.Trim()),
                ("hash", HashSimNowPassword(request.Password!)), ("now", now));
            await ExecuteSqlAsync(connection, $"""
                INSERT INTO {Table(schema, "UserRoles")} ("UserId", "RoleId", "AssignedAtUtc")
                VALUES (@userId, @roleId, @now)
                """, token, ("userId", userId), ("roleId", roleId), ("now", now));
            return "Account created with the selected role. Share the initial credentials privately; no email was sent.";
        }

        await ConfirmAccountAsync(connection, schema, request, token);
        var selectedId = Guid.Parse(request.UserId!);
        if (request.Operation == "set-password")
        {
            await ExecuteSqlAsync(connection, $"""
                UPDATE {Table(schema, "Users")}
                SET "PasswordHash" = @hash, "FailedLoginCount" = 0, "LockoutEndsAtUtc" = NULL
                WHERE "Id" = @id
                """, token, ("hash", HashSimNowPassword(request.Password!)), ("id", selectedId));
            return "Password updated and login lockout cleared. Existing SimNow sign-in tokens remain valid until they expire. No email was sent.";
        }

        // The locked user row blocks new FK-backed role assignments; lock current assignments
        // and role names as well so administrator protection is checked against stable roles.
        var assignedRoles = new List<string>();
        await using (var command = Command(connection, $"""
            SELECT r."Name" FROM {Table(schema, "UserRoles")} ur
            JOIN {Table(schema, "Roles")} r ON r."Id" = ur."RoleId"
            WHERE ur."UserId" = @id FOR SHARE OF ur, r
            """, ("id", selectedId)))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token)) assignedRoles.Add(reader.GetString(0));
        }
        if (assignedRoles.Any(IsAdministrator))
            throw new DatabaseAccountException("Administrator accounts are protected and cannot be removed.");

        await ExecuteSqlAsync(connection, $"""
            DELETE FROM {Table(schema, "Users")} WHERE "Id" = @id
            """, token, ("id", selectedId));
        return "Account and its role assignments removed. Existing SimNow sign-in tokens remain valid until they expire.";
    }

    private static string HashSimNowPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        byte[]? hash = null;
        try
        {
            hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 210_000, HashAlgorithmName.SHA256, 32);
            return $"PBKDF2$210000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
            if (hash is not null) CryptographicOperations.ZeroMemory(hash);
        }
    }
}
