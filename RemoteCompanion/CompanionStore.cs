using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
using FullStackLauncher.CompanionProtocol;
using FullStackLauncher.Services;

namespace FullStackRemoteCompanion;

internal sealed record ApprovedController(string Name, string Fingerprint, DateTimeOffset ApprovedAt);
internal sealed record StoredIdentity(int Version, string DeviceId, string Certificate, ApprovedController[] Controllers);

internal sealed class CompanionStore : IDisposable
{
    private const string Purpose = "FullStackLauncher.RemoteCompanion.Identity.v1";
    private readonly object _gate = new();
    private readonly string _path;
    private readonly bool _service;
    private ApprovedController[] _controllers;
    public string DeviceId { get; }
    public X509Certificate2 Certificate { get; }
    public string Fingerprint { get; }

    public CompanionStore(bool service)
    {
        _service = service;
        var folder = service ? Path.Combine(ServiceInstallation.DataDirectory, "identity")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FullStackLauncher", "remote-companion");
        SafePaths.RefuseReparse(folder);
        if (service) SafePaths.RequireServiceDirectory(folder, allowLocalServiceWrite: true);
        else Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "identity.dat");
        SafePaths.RefuseReparse(_path);
        if (File.Exists(_path))
        {
            try
            {
                if (service) SafePaths.RequireServiceFile(_path, true);
                if (new FileInfo(_path).Length is < 1 or > 1024 * 1024) throw new InvalidDataException();
                var encrypted = File.ReadAllBytes(_path);
                var plaintext = WindowsUserSecretProtection.Unprotect(encrypted, Purpose);
                StoredIdentity identity;
                try { identity = JsonSerializer.Deserialize<StoredIdentity>(plaintext, RemoteProtocol.Json) ?? throw new InvalidDataException(); }
                finally { CryptographicOperations.ZeroMemory(plaintext); }
                if (identity.Version != 1 || !Guid.TryParseExact(identity.DeviceId, "N", out _) || identity.Controllers is null || identity.Controllers.Length > 100 ||
                    identity.Controllers.Any(p => p is null || !RemoteProtocol.ValidFingerprint(p.Fingerprint) || p.Name != RemoteProtocol.CleanName(p.Name) || string.IsNullOrWhiteSpace(p.Name)) ||
                    identity.Controllers.Select(p => p.Fingerprint).Distinct(StringComparer.OrdinalIgnoreCase).Count() != identity.Controllers.Length) throw new InvalidDataException();
                var pfx = Convert.FromBase64String(identity.Certificate);
                try { Certificate = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable); }
                finally { CryptographicOperations.ZeroMemory(pfx); }
                if (!Certificate.HasPrivateKey || Certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow || Certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow) throw new InvalidDataException();
                DeviceId = identity.DeviceId;
                _controllers = identity.Controllers;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException or FormatException or InvalidDataException)
            {
                throw new InvalidDataException("Protected companion identity cannot be read. Existing data was preserved; networking remains stopped.");
            }
        }
        else
        {
            DeviceId = Guid.NewGuid().ToString("N");
            Certificate = RemoteProtocol.CreateCertificate("FullStackRemoteCompanion", true);
            _controllers = [];
            Save(_controllers);
        }
        Fingerprint = RemoteProtocol.Fingerprint(Certificate);
    }

    public ApprovedController[] Controllers { get { lock (_gate) return _controllers.ToArray(); } }
    public bool IsApproved(string fingerprint) { lock (_gate) return _controllers.Any(p => p.Fingerprint.Equals(fingerprint, StringComparison.OrdinalIgnoreCase)); }
    public void Approve(string name, string fingerprint)
    {
        lock (_gate)
        {
            var candidate = _controllers.Where(p => !p.Fingerprint.Equals(fingerprint, StringComparison.OrdinalIgnoreCase))
                .Append(new ApprovedController(name, fingerprint.ToUpperInvariant(), DateTimeOffset.UtcNow)).ToArray();
            if (candidate.Length > 100) throw new InvalidOperationException("Remove a paired PC before adding another (limit: 100).");
            Save(candidate);
            _controllers = candidate;
        }
    }
    public void Revoke(string fingerprint)
    {
        lock (_gate)
        {
            var candidate = _controllers.Where(p => !p.Fingerprint.Equals(fingerprint, StringComparison.OrdinalIgnoreCase)).ToArray();
            Save(candidate);
            _controllers = candidate;
        }
    }
    private void Save(ApprovedController[] candidate)
    {
        SafePaths.RefuseReparse(_path);
        if (_service)
        {
            SafePaths.RequireServiceDirectory(Path.GetDirectoryName(_path)!, true);
            if (File.Exists(_path)) SafePaths.RequireServiceFile(_path, true);
        }
        var pfx = Certificate.Export(X509ContentType.Pfx);
        byte[] plaintext;
        try { plaintext = JsonSerializer.SerializeToUtf8Bytes(new StoredIdentity(1, DeviceId, Convert.ToBase64String(pfx), candidate), RemoteProtocol.Json); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
        byte[] ciphertext;
        try { ciphertext = WindowsUserSecretProtection.Protect(plaintext, Purpose); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(ciphertext); stream.Flush(flushToDisk: true); }
            if (File.Exists(_path)) File.Replace(temporary, _path, null);
            else File.Move(temporary, _path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Dispose() => Certificate.Dispose();
}

internal static class SafePaths
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier LocalService = new(WellKnownSidType.LocalServiceSid, null);
    public static void RefuseReparse(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Companion paths must not contain symbolic links or junctions.");
        }
    }
    public static void CreateProtectedDirectory(string path, bool allowLocalServiceWrite)
    {
        RefuseReparse(path);
        if (Directory.Exists(path)) { RequireServiceDirectory(path, allowLocalServiceWrite); return; }
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(Administrators);
        Add(security, Administrators, FileSystemRights.FullControl);
        Add(security, System, FileSystemRights.FullControl);
        Add(security, LocalService, allowLocalServiceWrite ? FileSystemRights.Modify : FileSystemRights.ReadAndExecute);
        FileSystemAclExtensions.CreateDirectory(security, path);
        RefuseReparse(path);
        RequireServiceDirectory(path, allowLocalServiceWrite);
    }
    private static void Add(DirectorySecurity security, SecurityIdentifier sid, FileSystemRights rights) => security.AddAccessRule(
        new FileSystemAccessRule(sid, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
    public static void RequireServiceDirectory(string path, bool allowLocalServiceWrite)
    {
        RefuseReparse(path);
        if (!Directory.Exists(path)) throw new IOException("Protected service directory is missing. Install the service from the companion window.");
        var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (!security.AreAccessRulesProtected) throw new IOException("Companion service directory has unsafe inherited permissions.");
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier ?? throw new IOException("Companion service directory owner is unavailable.");
        if (!owner.Equals(Administrators) && !owner.Equals(System)) throw new IOException("Companion service directory has an unsafe owner.");
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();
        foreach (var rule in rules.Where(r => r.AccessControlType == AccessControlType.Allow))
        {
            var sid = (SecurityIdentifier)rule.IdentityReference;
            if (!sid.Equals(Administrators) && !sid.Equals(System) && !sid.Equals(LocalService)) throw new IOException("Companion service directory grants access to an unexpected account.");
            if (sid.Equals(LocalService) && !allowLocalServiceWrite && (rule.FileSystemRights & (FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)) != 0)
                throw new IOException("Companion installation directory allows service modification.");
            if (sid.Equals(LocalService) && (rule.FileSystemRights & (FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)) != 0)
                throw new IOException("Companion service directory permits unsafe permission changes.");
        }
    }
    public static void RequireServiceFile(string path, bool allowLocalServiceWrite)
    {
        RefuseReparse(path);
        var security = new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier ?? throw new IOException("Companion service file owner is unavailable.");
        if (!owner.Equals(Administrators) && !owner.Equals(System) && !(allowLocalServiceWrite && owner.Equals(LocalService)))
            throw new IOException("Companion service file has an unsafe owner.");
        foreach (var rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Where(r => r.AccessControlType == AccessControlType.Allow))
        {
            var sid = (SecurityIdentifier)rule.IdentityReference;
            if (!sid.Equals(Administrators) && !sid.Equals(System) && !sid.Equals(LocalService)) throw new IOException("Companion service file grants access to an unexpected account.");
            if (sid.Equals(LocalService) && !allowLocalServiceWrite && (rule.FileSystemRights & (FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)) != 0)
                throw new IOException("Companion service file allows unsafe modification.");
            if (sid.Equals(LocalService) && (rule.FileSystemRights & (FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)) != 0)
                throw new IOException("Companion service file permits unsafe permission changes.");
        }
    }
}
