namespace FullStackLauncher.Models;

/// <summary>Transient input only. Never save or log this object: Password contains a credential.</summary>
public sealed class DatabaseAccountRequest
{
    public string Operation { get; init; } = "list";
    public string? UserId { get; init; }
    public string? UserName { get; init; }
    public string? DisplayName { get; init; }
    public string? Email { get; init; }
    public string? Password { get; init; }
    public string? Role { get; init; }
    public string? ProjectId { get; init; }
    public string? Confirmation { get; init; }
    public string? AccountConfirmation { get; init; }
}

public sealed class DatabaseAccountResponse
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public bool OutcomeUncertain { get; init; }
    public IReadOnlyList<DatabaseAccount> Accounts { get; init; } = [];
    public IReadOnlyList<DatabaseAccountProject> Projects { get; init; } = [];
    public string AccountTable { get; init; } = "";
    public string SchemaKind { get; init; } = "";
    public bool RequiresProject { get; init; }
    public bool SupportsEmail { get; init; }
    public bool SupportsDisplayName { get; init; }
    public string DefaultRole { get; init; } = "";
    public IReadOnlyList<string> Roles { get; init; } = [];
    public bool CanCreate { get; init; }
    public bool CanSetPassword { get; init; }
    public string OperationNotice { get; init; } = "";
}

public sealed class DatabaseAccount
{
    public string Id { get; init; } = "";
    public string UserName { get; init; } = "";
    public string? DisplayName { get; init; }
    public string? Email { get; init; }
    public string Role { get; init; } = "";
    public string? ProjectName { get; init; }
    public string Status { get; init; } = "";
    public bool CanDelete { get; init; }
}

public sealed class DatabaseAccountProject
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
}

public sealed class DatabaseAccountException(string message, bool outcomeUncertain = false) : Exception(message)
{
    public bool OutcomeUncertain { get; } = outcomeUncertain;
}
