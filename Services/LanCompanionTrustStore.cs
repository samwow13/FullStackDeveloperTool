using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using FullStackLauncher.CompanionProtocol;

namespace FullStackLauncher.Services;

internal sealed record LanCompanionTrust(string DeviceId, string CertificateFingerprint, string ComputerName);

/// <summary>Controller identity and paired devices are one atomic current-user encrypted store.</summary>
internal sealed class LanCompanionTrustStore
{
    private const string ProtectionPurpose = "FullStackLauncher.RemoteCompanion.Controller.v1";
    private const int MaximumBytes = 512 * 1024;
    private readonly string _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FullStackLauncher", "remote-companion");
    private string StorePath => Path.Combine(_directory, "controller.dat");

    internal LanCompanionTrust? Find(string deviceId) => ReadTrustedDevices().SingleOrDefault(item => item.DeviceId == deviceId);

    internal IReadOnlyList<LanCompanionTrust> ReadTrustedDevices() => WithLock(() =>
    {
        var document = Load();
        try { return (IReadOnlyList<LanCompanionTrust>)document.Devices.ToArray(); }
        finally { CryptographicOperations.ZeroMemory(document.ControllerCertificate); }
    });

    internal X509Certificate2 GetControllerCertificate() => WithLock(() =>
    {
        var document = Load();
        try
        {
            if (document.ControllerCertificate.Length == 0)
            {
                using var created = RemoteProtocol.CreateCertificate("FullStackLauncher Controller", server: false);
                document.ControllerCertificate = created.Export(X509ContentType.Pfx);
                Save(document);
            }
            var certificate = X509CertificateLoader.LoadPkcs12(document.ControllerCertificate, null,
                X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
            if (!certificate.HasPrivateKey || DateTime.UtcNow < certificate.NotBefore.ToUniversalTime() || DateTime.UtcNow >= certificate.NotAfter.ToUniversalTime())
            {
                certificate.Dispose();
                throw new InvalidOperationException("Companion controller certificate is unavailable or expired. Review saved pairings before replacing its identity.");
            }
            return certificate;
        }
        finally { CryptographicOperations.ZeroMemory(document.ControllerCertificate); }
    });

    internal void Remember(LanCompanionTrust trust, string expectedControllerFingerprint) => WithLock(() =>
    {
        ValidateTrust(trust);
        var document = Load();
        try
        {
            if (document.ControllerCertificate.Length == 0) throw new InvalidOperationException("Controller identity changed during pairing. Pairing was not saved locally.");
            using var certificate = X509CertificateLoader.LoadPkcs12(document.ControllerCertificate, null, X509KeyStorageFlags.EphemeralKeySet);
            if (!RemoteProtocol.Fingerprint(certificate).Equals(expectedControllerFingerprint, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Controller identity changed during pairing. Pairing was not saved locally.");
            var existing = document.Devices.SingleOrDefault(item => item.DeviceId == trust.DeviceId);
            if (existing is not null && !existing.CertificateFingerprint.Equals(trust.CertificateFingerprint, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Saved companion identity changed. Review and forget its old pairing before pairing again.");
            document.Devices.RemoveAll(item => item.DeviceId == trust.DeviceId);
            document.Devices.Add(trust with { CertificateFingerprint = trust.CertificateFingerprint.ToUpperInvariant() });
            Save(document);
            return true;
        }
        finally { CryptographicOperations.ZeroMemory(document.ControllerCertificate); }
    });

    internal void Forget(string deviceId) => WithLock(() =>
    {
        if (!LanCompanionDiscoveryService.ValidDeviceId(deviceId)) throw new ArgumentException("Companion device ID is invalid.", nameof(deviceId));
        var document = Load();
        try
        {
            if (document.Devices.RemoveAll(item => item.DeviceId == deviceId) > 0) Save(document);
            return true;
        }
        finally { CryptographicOperations.ZeroMemory(document.ControllerCertificate); }
    });

    private T WithLock<T>(Func<T> action)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("Current Windows user identity is unavailable. Companion trust cannot be accessed.");
        using var mutex = new Mutex(false, @"Global\FullStackLauncher.RemoteCompanion.ControllerStore." + sid + "." +
            Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(StorePath.ToUpperInvariant())))[..24]);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new InvalidOperationException("Companion pairing store is busy. Wait, then try again.");
            return action();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or JsonException or NotSupportedException)
        {
            throw new InvalidOperationException("Companion pairing store could not be read or saved. Existing identity and pairings were preserved; repair the current Windows user's protected store before retrying.", exception);
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }

    private StoreDocument Load()
    {
        if (!Exists(StorePath))
        {
            if (Exists(StorePath + ".bak")) throw new InvalidOperationException("Companion pairing store is missing while its protected backup remains. Recover the store before pairing again.");
            return new StoreDocument();
        }
        using var file = new FileStream(StorePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 1 or > MaximumBytes) throw new InvalidDataException("Companion pairing store has an invalid size.");
        var protectedBytes = new byte[(int)file.Length];
        file.ReadExactly(protectedBytes);
        var bytes = WindowsUserSecretProtection.Unprotect(protectedBytes, ProtectionPurpose);
        try
        {
            var document = JsonSerializer.Deserialize<StoreDocument>(bytes, RemoteProtocol.Json)
                ?? throw new InvalidDataException("Companion pairing store is invalid.");
            try { ValidateDocument(document); }
            catch { if (document.ControllerCertificate is not null) CryptographicOperations.ZeroMemory(document.ControllerCertificate); throw; }
            return document;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private void Save(StoreDocument document)
    {
        ValidateDocument(document);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, RemoteProtocol.Json);
        byte[] protectedBytes;
        try { protectedBytes = WindowsUserSecretProtection.Protect(bytes, ProtectionPurpose); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        if (protectedBytes.Length > MaximumBytes) throw new InvalidDataException("Companion pairing store exceeds its size limit.");
        Directory.CreateDirectory(_directory);
        var temporary = StorePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(protectedBytes); file.Flush(flushToDisk: true); }
            if (File.Exists(StorePath)) File.Replace(temporary, StorePath, StorePath + ".bak", ignoreMetadataErrors: false);
            else File.Move(temporary, StorePath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void ValidateDocument(StoreDocument document)
    {
        if (document.Version != RemoteProtocol.Version || document.ControllerCertificate is null || document.ControllerCertificate.Length > 65536 ||
            document.Devices is null || document.Devices.Count > 128 || document.Devices.Count > 0 && document.ControllerCertificate.Length == 0 ||
            document.Devices.Select(item => item?.DeviceId).Distinct(StringComparer.Ordinal).Count() != document.Devices.Count)
            throw new InvalidDataException("Companion pairing store is invalid or uses an unsupported version.");
        foreach (var trust in document.Devices) ValidateTrust(trust);
    }

    private static void ValidateTrust(LanCompanionTrust? trust)
    {
        if (trust is null || !LanCompanionDiscoveryService.ValidDeviceId(trust.DeviceId) || !RemoteProtocol.ValidFingerprint(trust.CertificateFingerprint) ||
            !LanCompanionDiscoveryService.ValidName(trust.ComputerName)) throw new InvalidDataException("Companion pairing identity is invalid.");
    }

    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private sealed class StoreDocument
    {
        [JsonRequired] public int Version { get; set; } = RemoteProtocol.Version;
        [JsonRequired] public byte[] ControllerCertificate { get; set; } = [];
        [JsonRequired] public List<LanCompanionTrust> Devices { get; set; } = [];
    }
}
