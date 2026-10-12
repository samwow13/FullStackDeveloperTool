using System.Security;

namespace FullStackLauncher.Models;

public enum LanRemoteAction { SystemInfo, Processes }

/// <summary>Explicitly entered credentials, retained only in this process's memory.</summary>
public sealed class LanRemoteCredentials : IDisposable
{
    private readonly object _gate = new();
    private SecureString? _password;

    public LanRemoteCredentials(string userName, SecureString password)
    {
        ArgumentNullException.ThrowIfNull(password);
        userName = userName.Trim();
        if (userName.Length is < 1 or > 256 || userName.Any(char.IsControl))
            throw new ArgumentException("Enter a Windows account, such as PCNAME\\user or DOMAIN\\user.");
        if (password.Length is < 1 or > 1024)
            throw new ArgumentException("Enter the account password. Windows Hello PINs cannot authenticate WinRM.");
        UserName = userName;
        _password = password.Copy();
        _password.MakeReadOnly();
    }

    public string UserName { get; }

    internal SecureString CopyPassword()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_password is null, this);
            return _password!.Copy();
        }
    }

    internal LanRemoteCredentials Copy()
    {
        using var password = CopyPassword();
        return new(UserName, password);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _password?.Dispose();
            _password = null;
        }
    }
}

public sealed record LanRemoteIdentity(string ComputerName, string OperatingSystem, string Identity, string AccountSid)
{
    public string DisplayName => ComputerName;
}

/// <summary>
/// Authenticated target evidence and an owned credential copy. Every read creates and removes
/// its own WinRM transport. Disposing this session releases its in-memory password.
/// </summary>
public sealed class LanRemoteSession : IDisposable
{
    private readonly object _gate = new();
    private LanRemoteCredentials? _credentials;

    internal LanRemoteSession(string target, string address, bool useHttps, LanRemoteCredentials credentials,
        LanRemoteIdentity identity, DateTimeOffset authenticatedAt)
    {
        Target = target;
        Address = address;
        UseHttps = useHttps;
        UserName = credentials.UserName;
        Identity = identity;
        AuthenticatedAt = authenticatedAt;
        _credentials = credentials;
    }

    public string Target { get; }
    public string Address { get; }
    public bool UseHttps { get; }
    public string UserName { get; }
    public LanRemoteIdentity Identity { get; }
    public string DisplayName => Identity.DisplayName;
    public string ComputerName => Identity.ComputerName;
    public string OperatingSystem => Identity.OperatingSystem;
    public DateTimeOffset AuthenticatedAt { get; }

    internal LanRemoteCredentials CopyCredentials()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_credentials is null, this);
            return _credentials!.Copy();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _credentials?.Dispose();
            _credentials = null;
        }
    }
}

public sealed record LanRemoteCommandResult(string Title, string Text, DateTimeOffset CompletedAt);
