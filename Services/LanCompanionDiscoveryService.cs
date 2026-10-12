using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using FullStackLauncher.CompanionProtocol;
using FullStackLauncher.Models;

namespace FullStackLauncher.Services;

/// <summary>Bounded unauthenticated discovery on one directly connected private IPv4 LAN.</summary>
public sealed class LanCompanionDiscoveryService
{
    private const int MaximumDevices = 128;
    private const int MaximumDatagrams = 512;

    public Task<IReadOnlyList<LanCompanionDevice>> DiscoverAsync(LanNetwork network, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(network);
        var current = GetCurrentNetwork(network);
        var (subnet, mask) = GetSubnet(current);
        var broadcast = new IPAddress(subnet.Select((value, index) => (byte)(value | ~mask[index])).ToArray());
        return ExchangeAsync(current, broadcast, null, cancellationToken);
    }

    public async Task<LanCompanionDevice?> ProbeAsync(string privateIPv4, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(privateIPv4, out var address) || !RemoteProtocol.IsPrivate(address) ||
            LanDiscoveryService.IsLocalAddress(address.ToString()))
            throw new ArgumentException("Choose another PC's directly connected private IPv4 LAN address.", nameof(privateIPv4));
        var network = new LanDiscoveryService().GetNetworks().FirstOrDefault(candidate => Contains(candidate, address))
            ?? throw new ArgumentException("Companion address must be on a directly connected private IPv4 LAN.", nameof(privateIPv4));
        var devices = await ExchangeAsync(network, address, address, cancellationToken).ConfigureAwait(false);
        return devices.SingleOrDefault();
    }

    internal static bool ValidDeviceId(string? value) => value is { Length: >= 32 and <= 36 } &&
        (Guid.TryParseExact(value, "N", out _) || Guid.TryParseExact(value, "D", out _));

    internal static bool ValidName(string? value, int maximum = 80) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximum && !value.Any(char.IsControl);

    internal static void ValidateDevice(LanCompanionDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!ValidDeviceId(device.DeviceId) || !RemoteProtocol.ValidFingerprint(device.CertificateFingerprint) ||
            !ValidName(device.ComputerName) || device.Advertisement.Kind != RemoteProtocol.AdvertisementKind ||
            device.Advertisement.Version != RemoteProtocol.Version || device.Advertisement.Port != RemoteProtocol.ManagementPort)
            throw new ArgumentException("Companion advertisement is invalid or uses an unsupported version.", nameof(device));
        if (!IPAddress.TryParse(device.Address, out var address) || !RemoteProtocol.IsPrivate(address) ||
            !LanDiscoveryService.IsAllowedLanAddress(device.Address) || LanDiscoveryService.IsLocalAddress(device.Address))
            throw new InvalidOperationException("Companion must be another PC on a directly connected private IPv4 LAN.");
    }

    private static async Task<IReadOnlyList<LanCompanionDevice>> ExchangeAsync(LanNetwork network,
        IPAddress destination, IPAddress? onlyAddress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var local = IPAddress.Parse(network.LocalAddress);
        var localAddresses = new LanDiscoveryService().GetNetworks().Select(item => item.LocalAddress).ToHashSet(StringComparer.Ordinal);
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var query = JsonSerializer.SerializeToUtf8Bytes(new CompanionDiscoveryQuery(RemoteProtocol.DiscoveryKind, RemoteProtocol.Version, nonce), RemoteProtocol.Json);
        using var client = new UdpClient(new IPEndPoint(local, 0));
        client.EnableBroadcast = true;
        client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.IpTimeToLive, 1);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(1500));
        var found = new Dictionary<string, LanCompanionDevice>(StringComparer.Ordinal);
        var conflicted = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            await client.SendAsync(query, new IPEndPoint(destination, RemoteProtocol.DiscoveryPort), timeout.Token).ConfigureAwait(false);
            for (var receivedCount = 0; receivedCount < MaximumDatagrams && found.Count < MaximumDevices; receivedCount++)
            {
                var received = await client.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                var sender = received.RemoteEndPoint;
                if (received.Buffer.Length is < 1 or > RemoteProtocol.MaximumDiscoveryBytes || sender.Port != RemoteProtocol.DiscoveryPort ||
                    !Contains(network, sender.Address) || localAddresses.Contains(sender.Address.ToString()) ||
                    onlyAddress is not null && !onlyAddress.Equals(sender.Address)) continue;
                CompanionAdvertisement? advertisement;
                try { advertisement = JsonSerializer.Deserialize<CompanionAdvertisement>(received.Buffer, RemoteProtocol.Json); }
                catch (JsonException) { continue; }
                if (advertisement is null || advertisement.Kind != RemoteProtocol.AdvertisementKind || advertisement.Version != RemoteProtocol.Version ||
                    advertisement.Nonce != nonce || advertisement.Port != RemoteProtocol.ManagementPort || !ValidDeviceId(advertisement.DeviceId) ||
                    !ValidName(advertisement.ComputerName) || !RemoteProtocol.ValidFingerprint(advertisement.CertificateFingerprint) ||
                    conflicted.Contains(advertisement.DeviceId)) continue;
                var device = new LanCompanionDevice(sender.Address.ToString(), advertisement, DateTimeOffset.UtcNow);
                if (found.TryGetValue(device.DeviceId, out var previous) &&
                    (previous.Address != device.Address || !previous.CertificateFingerprint.Equals(device.CertificateFingerprint, StringComparison.OrdinalIgnoreCase)))
                {
                    found.Remove(device.DeviceId);
                    conflicted.Add(device.DeviceId);
                    continue;
                }
                if (found.Values.Any(other => other.Address == device.Address && other.DeviceId != device.DeviceId))
                {
                    foreach (var other in found.Values.Where(other => other.Address == device.Address).ToArray())
                    { found.Remove(other.DeviceId); conflicted.Add(other.DeviceId); }
                    conflicted.Add(device.DeviceId);
                    continue;
                }
                found[device.DeviceId] = device;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (SocketException) when (onlyAddress is not null) { }
        cancellationToken.ThrowIfCancellationRequested();
        return found.Values.OrderBy(item => item.ComputerName, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Address, StringComparer.Ordinal).ToArray();
    }

    private static LanNetwork GetCurrentNetwork(LanNetwork network) => new LanDiscoveryService().GetNetworks().FirstOrDefault(candidate =>
        candidate.InterfaceId == network.InterfaceId && candidate.LocalAddress == network.LocalAddress && candidate.Cidr == network.Cidr)
        ?? throw new InvalidOperationException("Selected LAN adapter is no longer connected. Refresh adapters.");

    private static bool Contains(LanNetwork network, IPAddress address)
    {
        if (!RemoteProtocol.IsPrivate(address)) return false;
        var (subnet, mask) = GetSubnet(network);
        var bytes = address.GetAddressBytes();
        if (!Enumerable.Range(0, 4).All(index => (bytes[index] & mask[index]) == subnet[index])) return false;
        var networkAddress = bytes.SequenceEqual(subnet);
        var broadcast = Enumerable.Range(0, 4).All(index => bytes[index] == (byte)(subnet[index] | ~mask[index]));
        return !networkAddress && !broadcast;
    }

    private static (byte[] Subnet, byte[] Mask) GetSubnet(LanNetwork network)
    {
        var pieces = network.Cidr.Split('/');
        if (pieces.Length != 2 || !IPAddress.TryParse(pieces[0], out var address) || !RemoteProtocol.IsPrivate(address) ||
            !int.TryParse(pieces[1], out var prefix) || prefix is < 1 or > 30)
            throw new InvalidDataException("Selected LAN subnet cannot support companion discovery.");
        var mask = Enumerable.Range(0, 4).Select(index => (byte)(0xff << Math.Clamp(8 - (prefix - index * 8), 0, 8))).ToArray();
        var bytes = address.GetAddressBytes();
        return (Enumerable.Range(0, 4).Select(index => (byte)(bytes[index] & mask[index])).ToArray(), mask);
    }
}
