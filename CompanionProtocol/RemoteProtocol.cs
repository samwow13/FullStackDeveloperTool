using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace FullStackLauncher.CompanionProtocol;

public static class RemoteProtocol
{
    public const int Version = 1;
    public const int DiscoveryPort = 45873;
    public const int ManagementPort = 45874;
    public const int MaximumFrameBytes = 512 * 1024;
    public const int MaximumDesktopJpegBytes = 8 * 1024 * 1024;
    public const int MaximumDiscoveryBytes = 2048;
    public const string DiscoveryKind = "FullStackLauncher.Companion.Discover";
    public const string AdvertisementKind = "FullStackLauncher.Companion.Advertise";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 16 };

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length is < 1 or > MaximumFrameBytes) throw new InvalidDataException("Companion message exceeds its size limit.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is < 1 or > MaximumFrameBytes) throw new InvalidDataException("Companion message exceeds its size limit.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("Companion message is invalid.");
    }

    public static string Fingerprint(X509Certificate certificate) => Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

    public static bool ValidFingerprint(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    public static string PairingProof(string code, string serverFingerprint, string controllerFingerprint, string deviceId, string requestId, bool serverProof = false)
    {
        var normalized = new string(code.Where(c => c != '-' && !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
        if (normalized.Length != 12 || normalized.Any(c => !"23456789ABCDEFGHJKLMNPQRSTUVWXYZ".Contains(c)))
            throw new ArgumentException("Enter the 12-character pairing code shown by the companion.");
        var key = Encoding.UTF8.GetBytes(normalized);
        var message = Encoding.UTF8.GetBytes($"FullStackLauncher.Pair.v1.{(serverProof ? "server" : "client")}\n{serverFingerprint.ToUpperInvariant()}\n{controllerFingerprint.ToUpperInvariant()}\n{deviceId}\n{requestId}");
        try { return Convert.ToHexString(HMACSHA256.HashData(key, message)); }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(message); }
    }

    public static X509Certificate2 CreateCertificate(string subject, bool server)
    {
        using var key = RSA.Create(3072);
        var request = new CertificateRequest(new X500DistinguishedName("CN=" + subject), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(server ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2") }, true));
        var now = DateTimeOffset.UtcNow;
        using var generated = request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(5));
        var bytes = generated.Export(X509ContentType.Pfx);
        try { return X509CertificateLoader.LoadPkcs12(bytes, null, X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168 || b[0] == 169 && b[1] == 254;
    }

    public static bool SamePrivateLan(IPAddress remote, IPAddress? local = null)
    {
        if (!IsPrivate(remote)) return false;
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            try
            {
                foreach (var entry in adapter.GetIPProperties().UnicastAddresses)
                {
                    if (!IsPrivate(entry.Address) || local is not null && !entry.Address.Equals(local)) continue;
                    var a = entry.Address.GetAddressBytes(); var b = remote.GetAddressBytes(); var m = entry.IPv4Mask.GetAddressBytes();
                    if (Enumerable.Range(0, 4).All(i => (a[i] & m[i]) == (b[i] & m[i]))) return true;
                }
            }
            catch (NetworkInformationException) { }
        }
        return false;
    }

    public static string CleanName(string? value, int maximum = 80) => new((value ?? "").Where(c => !char.IsControl(c)).Take(maximum).ToArray());
}

public sealed record CompanionDiscoveryQuery(string Kind, int Version, string Nonce);
public sealed record CompanionAdvertisement
{
    public string Kind { get; init; } = RemoteProtocol.AdvertisementKind;
    public int Version { get; init; } = RemoteProtocol.Version;
    public string Nonce { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public string ComputerName { get; init; } = "";
    public int Port { get; init; } = RemoteProtocol.ManagementPort;
    public string CertificateFingerprint { get; init; } = "";
    public bool PairingAvailable { get; init; }
}

public sealed record CompanionRequest
{
    public int Version { get; init; } = RemoteProtocol.Version;
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");
    public string Action { get; init; } = "ping";
    public string DeviceId { get; init; } = "";
    public string ControllerName { get; init; } = "";
    public string PairingProof { get; init; } = "";
    public RemoteDesktopOptions? DesktopOptions { get; init; }
    public int DesktopVersion { get; init; }
}

public sealed record CompanionResponse
{
    public int Version { get; init; } = RemoteProtocol.Version;
    public string RequestId { get; init; } = "";
    public bool Success { get; init; }
    public string ErrorCode { get; init; } = "";
    public string Message { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public string ComputerName { get; init; } = "";
    public string OperatingSystem { get; init; } = "";
    public string Text { get; init; } = "";
    public string PairingProof { get; init; } = "";
    public string[] Capabilities { get; init; } = ["systemInfo", "processes"];
    public RemoteDesktopMonitor[] Monitors { get; init; } = [];
}

public sealed record RemoteDesktopOptions
{
    public long Revision { get; init; }
    public string MonitorId { get; init; } = "";
    public int MaxFramesPerSecond { get; init; } = 30;
    public int MaxWidth { get; init; } = 1920;
    public int JpegQuality { get; init; } = 85;
}

public sealed record RemoteDesktopMonitor(string Id, string Name, int Left, int Top, int Width, int Height, bool Primary);

public sealed record RemoteDesktopFrame
{
    public long OptionsRevision { get; init; }
    public string MonitorId { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public int SourceWidth { get; init; }
    public int SourceHeight { get; init; }
    public long Sequence { get; init; }
    public int JpegLength { get; init; }
    public RemoteDesktopMonitor[] Monitors { get; init; } = [];
    public double CaptureMilliseconds { get; init; }
}

public sealed record RemoteDesktopCommand
{
    public string Kind { get; init; } = "heartbeat";
    public RemoteDesktopOptions? Options { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public string Button { get; init; } = "";
    public bool Down { get; init; }
    public int VirtualKey { get; init; }
    public bool Extended { get; init; }
    public int WheelDelta { get; init; }
    public long Sequence { get; init; }
}
