using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using FullStackLauncher.Models;
using Npgsql;

namespace FullStackLauncher.Services;

/// <summary>Direct, bounded PostgreSQL maintenance for the explicitly recognized account schemas.</summary>
public static partial class DatabaseAccountService
{
    private const int MaximumAccounts = 5000;
    private const int MaximumProjects = 2000;

    public static async Task<DatabaseAccountResponse> ExecuteAsync(DatabaseConnectionSource source,
        string database, DatabaseAccountRequest request, CancellationToken token)
    {
        var commitAttempted = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            if (request.Operation is not ("list" or "create" or "set-password" or "delete"))
                throw new DatabaseAccountException("Choose a supported account operation.");
            if (string.IsNullOrWhiteSpace(database) || database.Contains('\0') || Encoding.UTF8.GetByteCount(database) > 63)
                throw new DatabaseAccountException("Select an exact PostgreSQL database name.");
            if (request.Operation != "list" && request.Confirmation != $"{source.Server} / {database}")
                throw new DatabaseAccountException("Type the selected server, port, and database exactly to confirm this change.");
            await using var connection = source.CreateConnection(database);
            var builder = new NpgsqlConnectionStringBuilder(connection.ConnectionString)
            {
                Enlist = false, Multiplexing = false, ApplicationName = "Full Stack Launcher Accounts"
            };
            if (string.IsNullOrWhiteSpace(builder.Host) || builder.Host.Contains(','))
                throw new DatabaseAccountException("Account management requires one explicit PostgreSQL server.");
            connection.ConnectionString = builder.ConnectionString;
            await connection.OpenAsync(timeout.Token);
            await using var transaction = await connection.BeginTransactionAsync(
                request.Operation == "list" ? IsolationLevel.RepeatableRead : IsolationLevel.ReadCommitted, timeout.Token);
            await ExecuteSqlAsync(connection, "SET LOCAL lock_timeout = '10s'; SET LOCAL statement_timeout = '30s'", timeout.Token);
            if (request.Operation == "list")
                await ExecuteSqlAsync(connection, "SET TRANSACTION READ ONLY", timeout.Token);
            var schema = await ReadSchemaAsync(connection, timeout.Token);
            if (request.Operation == "list")
                return schema.Kind == "SimNow" ? await ListSimNowAsync(connection, schema, timeout.Token)
                    : await ListIdentityAsync(connection, schema, timeout.Token);

            ValidateMutation(request, schema);
            var message = schema.Kind == "SimNow"
                ? await MutateSimNowAsync(connection, schema, request, timeout.Token)
                : await MutateIdentityAsync(connection, schema, request, timeout.Token);
            commitAttempted = true;
            await transaction.CommitAsync(timeout.Token);
            return new DatabaseAccountResponse { Success = true, Message = message };
        }
        catch (Exception exception)
        {
            if (commitAttempted)
                throw new DatabaseAccountException("The database did not confirm the final outcome. The change may have completed. Refresh accounts and verify the result before making another change.", true);
            if (exception is DatabaseAccountException) throw;
            if (exception is OperationCanceledException && token.IsCancellationRequested) throw;
            throw new DatabaseAccountException(exception switch
            {
                PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } =>
                    "That username, email, or role is already in use. Refresh accounts and use unique account details.",
                PostgresException { SqlState: PostgresErrorCodes.InsufficientPrivilege } =>
                    "The database login does not have permission for this account operation.",
                PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation } =>
                    "Related records prevent this change. No account changes were committed. Refresh accounts and use the application's account tools for this record.",
                OperationCanceledException => "Account maintenance exceeded two minutes. No account changes were committed. Refresh accounts before trying again.",
                _ => "Account maintenance failed. Check the selected database connection, permissions, and account schema, then refresh accounts. No account changes were committed."
            });
        }
    }

    private static void ValidateMutation(DatabaseAccountRequest request, AccountSchema schema)
    {
        if (request.Operation == "create" && !schema.CanCreate)
            throw new DatabaseAccountException("This account table has additional required fields or unsupported constraints. Creation is unavailable for this schema.");
        if (schema.Kind == "Identity")
            throw new DatabaseAccountException("This Identity account table is available for viewing. Its application-specific account rules are unknown, so changes are unavailable.");
        if (request.Operation is "set-password" or "delete" &&
            (!Guid.TryParse(request.UserId, out var id) || id == Guid.Empty || string.IsNullOrEmpty(request.AccountConfirmation)))
            throw new DatabaseAccountException("Select an account and type its current username to confirm this change.");
        if (request.Operation == "delete" && !schema.CanDelete)
            throw new DatabaseAccountException("This database has unsupported account dependencies or retention constraints. Remove accounts through the application's account tools.");
        if (request.Operation is "create" or "set-password")
        {
            var password = request.Password;
            if (string.IsNullOrEmpty(password) || password.Length is < 12 or > 1024 || !password.Any(value => value is >= 'A' and <= 'Z')
                || !password.Any(value => value is >= 'a' and <= 'z') || !password.Any(value => value is >= '0' and <= '9')
                || password.All(value => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9'))
                throw new DatabaseAccountException("Use a password of 12–1024 characters with uppercase, lowercase, a number, and a symbol.");
        }
        if (request.Operation != "create") return;
        if (string.IsNullOrWhiteSpace(request.UserName) || request.UserName.Trim().Length > 256 || request.UserName.Contains('\0'))
            throw new DatabaseAccountException("Provide a username of 1–256 characters.");
        if (schema.Kind == "SimNow")
        {
            if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Trim().Length > 128 || request.DisplayName.Contains('\0'))
                throw new DatabaseAccountException("Provide a display name of 1–128 characters.");
            if (request.Role is not ("Admin" or "Member"))
                throw new DatabaseAccountException("Choose Admin or Member for the account role.");
        }
        else
        {
            const string allowed = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._@+";
            if (request.UserName.Trim().Any(character => !allowed.Contains(character)))
                throw new DatabaseAccountException("Use a username containing only letters, numbers, and - . _ @ +.");
            if (string.IsNullOrWhiteSpace(request.Email) || request.Email.Trim().Length > 256 ||
                !new EmailAddressAttribute().IsValid(request.Email.Trim()))
                throw new DatabaseAccountException("Provide a valid email address of up to 256 characters.");
            if (request.Role is not ("Customer" or "Administrator"))
                throw new DatabaseAccountException("Choose Customer or Administrator for the account role.");
            if (request.Role == "Customer" && (!Guid.TryParse(request.ProjectId, out var project) || project == Guid.Empty))
                throw new DatabaseAccountException("Choose an active project for the customer.");
        }
    }

    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    private static string Table(AccountSchema schema, string name) => $"{Quote(schema.Namespace)}.{Quote(name)}";
    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql, params (string Name, object? Value)[] values)
    {
        var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 30 };
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    private static async Task<int> ExecuteSqlAsync(NpgsqlConnection connection, string sql, CancellationToken token,
        params (string Name, object? Value)[] values)
    {
        await using var command = Command(connection, sql, values);
        return await command.ExecuteNonQueryAsync(token);
    }
    private static async Task<bool> ExistsAsync(NpgsqlConnection connection, string sql, CancellationToken token,
        params (string Name, object? Value)[] values)
    {
        await using var command = Command(connection, sql, values);
        return (bool)(await command.ExecuteScalarAsync(token) ?? false);
    }
    private static string NormalizeIdentity(string value) => value.Normalize().ToUpperInvariant();
    private static bool IsAdministrator(string role) => role.Equals("Administrator", StringComparison.OrdinalIgnoreCase)
        || role.Equals("Admin", StringComparison.OrdinalIgnoreCase);

    private static async Task ConfirmAccountAsync(NpgsqlConnection connection, AccountSchema schema,
        DatabaseAccountRequest request, CancellationToken token)
    {
        var userName = schema.Kind == "SimNow" ? "Username" : "UserName";
        await using var command = Command(connection,
            $"SELECT {Quote(userName)} FROM {Table(schema, schema.UserTable)} WHERE \"Id\" = @id FOR UPDATE",
            ("id", Guid.Parse(request.UserId!)));
        var actualName = await command.ExecuteScalarAsync(token);
        if (actualName is null) throw new DatabaseAccountException("The account no longer exists. Refresh the account list.");
        if (!string.Equals(actualName as string, request.AccountConfirmation, StringComparison.Ordinal))
            throw new DatabaseAccountException("The account confirmation does not match its current username. Refresh and confirm the account again.");
    }

    private static DatabaseAccountResponse ListResponse(AccountSchema schema, IReadOnlyList<DatabaseAccount> accounts,
        IReadOnlyList<DatabaseAccountProject> projects, IReadOnlyList<string> roles)
        => new()
        {
            Success = true, Message = $"Loaded {accounts.Count} accounts from {schema.Namespace}.{schema.UserTable}.",
            Accounts = accounts, Projects = projects, AccountTable = $"{schema.Namespace}.{schema.UserTable}",
            SchemaKind = schema.Kind, RequiresProject = schema.Kind == "CRM", SupportsEmail = schema.Kind != "SimNow",
            SupportsDisplayName = schema.Kind == "SimNow", DefaultRole = schema.Kind == "SimNow" ? "Member" : "Customer",
            Roles = roles, CanCreate = schema.CanCreate, CanSetPassword = schema.Kind is "CRM" or "SimNow",
            OperationNotice = (schema.Kind == "SimNow"
                ? "SimNow accounts: new usernames are saved in lowercase. Password changes clear lockout; existing sign-in tokens remain valid until they expire. Administrator accounts cannot be deleted."
                : schema.Kind == "CRM"
                    ? "CRM accounts: customers require an active project. Password changes invalidate sessions and reset links. Administrator accounts and customers with unresolved Stripe billing cannot be deleted here."
                    : "Identity accounts are available for viewing. Changes require a recognized application schema.")
                + (schema.Kind != "Identity" && !schema.CanCreate
                    ? " Account creation is unavailable because required fields or account constraints differ from the supported schema." : "")
                + (schema.Kind != "Identity" && !schema.CanDelete
                    ? " Removal is unavailable because related records do not match the supported cleanup rules." : "")
        };

    private sealed class AccountSchema(string @namespace, string kind, Dictionary<string, TableShape> tables,
        IReadOnlyList<ForeignKeyShape> foreignKeys)
    {
        public string Namespace { get; } = @namespace;
        public string Kind { get; } = kind;
        public string UserTable => Kind == "SimNow" ? "Users" : "AspNetUsers";
        public Dictionary<string, TableShape> Tables { get; } = tables;
        public IReadOnlyList<ForeignKeyShape> ForeignKeys { get; } = foreignKeys;
        public bool CanCreate { get; set; }
        public bool CanDelete { get; set; }
    }

    private sealed class TableShape(string @namespace, string name)
    {
        public string Namespace { get; } = @namespace;
        public string Name { get; } = name;
        public Dictionary<string, ColumnShape> Columns { get; } = new(StringComparer.Ordinal);
        public HashSet<string> PrimaryKey { get; } = new(StringComparer.Ordinal);
        public HashSet<string> UniqueColumns { get; } = new(StringComparer.Ordinal);
        public bool Has(params string[] columns) => columns.All(Columns.ContainsKey);
        public bool Type(string column, params string[] types) => Columns.TryGetValue(column, out var value) && types.Contains(value.Type);
        public bool SupportsInsert(params string[] supplied) => Columns.All(column => supplied.Contains(column.Key)
            || !column.Value.Required || column.Value.HasDefault);
    }
    private sealed record ColumnShape(string Type, bool Required, bool HasDefault);
    private sealed record ForeignKeyShape(string SourceSchema, string SourceTable, string[] SourceColumns,
        string TargetSchema, string TargetTable, string[] TargetColumns, string DeleteAction);
}
