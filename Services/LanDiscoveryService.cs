using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using FullStackLauncher.Models;

namespace FullStackLauncher.Services;

/// <summary>
/// Performs bounded, cancellable reachability discovery on directly connected private IPv4 networks.
/// Discovery opens no authenticated sessions and never changes a remote computer's configuration.
/// </summary>
public sealed class LanDiscoveryService
{
    private const int MaximumScanAddresses = 1024;
    private const int MaximumConcurrentHosts = 48;
    private const int ProbeTimeoutMilliseconds = 600;
    private const int NameTimeoutMilliseconds = 800;
    private const int ManualResolveTimeoutMilliseconds = 3000;

    public IReadOnlyList<LanNetwork> GetNetworks()
    {
        var networks = new List<LanNetwork>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;
            try
            {
                var properties = adapter.GetIPProperties();
                var hasGateway = properties.GatewayAddresses.Any(gateway =>
                    gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any));
                foreach (var entry in properties.UnicastAddresses)
                {
                    if (entry.Address.AddressFamily != AddressFamily.InterNetwork || !IsPrivateAddress(entry.Address)) continue;
                    var local = ToUInt32(entry.Address);
                    var prefix = PrefixFromMask(entry.IPv4Mask);
                    if (prefix is null) continue;
                    var subnet = new Ipv4Subnet(local & Mask(prefix.Value), prefix.Value);
                    if (!IsPrivateAddress(FromUInt32(subnet.Network)) || !IsPrivateAddress(FromUInt32(subnet.LastAddress))) continue;
                    var suggested = new Ipv4Subnet(local & Mask(Math.Max(prefix.Value, 24)), Math.Max(prefix.Value, 24));
                    var gatewayAddress = properties.GatewayAddresses.FirstOrDefault(gateway =>
                        gateway.Address.AddressFamily == AddressFamily.InterNetwork && subnet.ContainsUsableHost(ToUInt32(gateway.Address)))?.Address.ToString() ?? string.Empty;
                    networks.Add(new LanNetwork(adapter.Name, entry.Address.ToString(), subnet.ToString(), suggested.ToString())
                    {
                        InterfaceId = adapter.Id,
                        HasDefaultGateway = hasGateway,
                        GatewayAddress = gatewayAddress
                    });
                }
            }
            catch (NetworkInformationException) { }
            catch (PlatformNotSupportedException) { }
        }
        return networks.DistinctBy(network => (network.InterfaceId, network.LocalAddress, network.Cidr))
            .OrderByDescending(network => network.HasDefaultGateway)
            .ThenBy(network => network.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(network => ToUInt32(IPAddress.Parse(network.LocalAddress))).ToArray();
    }

    public async Task<IReadOnlyList<LanComputer>> DiscoverAsync(
        LanNetwork network, string cidr, IProgress<LanDiscoveryProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(network);
        cancellationToken.ThrowIfCancellationRequested();
        var connected = GetNetworks();
        var current = connected.FirstOrDefault(candidate => candidate.InterfaceId == network.InterfaceId &&
            candidate.LocalAddress == network.LocalAddress && candidate.Cidr == network.Cidr)
            ?? throw new InvalidOperationException("The selected LAN adapter is no longer connected. Refresh the adapter list.");
        var actual = ParseCidr(current.Cidr);
        var requested = ParseCidr(cidr);
        if (requested.AddressCount > MaximumScanAddresses)
            throw new ArgumentException("Scan at most 1024 addresses. Choose a /22 or smaller range, such as /24.", nameof(cidr));
        if (!actual.Contains(requested.Network) || !actual.Contains(requested.LastAddress) ||
            !IsPrivateAddress(FromUInt32(requested.Network)) || !IsPrivateAddress(FromUInt32(requested.LastAddress)))
            throw new ArgumentException("The scan range must stay inside the selected directly connected private LAN.", nameof(cidr));

        var addresses = EnumerateHosts(requested, actual).ToArray();
        var localAddresses = connected.Select(candidate => candidate.LocalAddress).ToHashSet(StringComparer.Ordinal);
        var results = new ConcurrentDictionary<string, LanComputer>(StringComparer.Ordinal);
        var probed = new ConcurrentDictionary<string, LanComputer>(StringComparer.Ordinal);
        var metadataTask = ObserveMetadataAsync(current, cancellationToken);
        var identification = new LanDeviceIdentificationService();
        var completed = 0;
        var progressGate = new object();
        progress?.Report(new LanDiscoveryProgress(0, addresses.Length, null));
        var scanTask = Parallel.ForEachAsync(addresses, new ParallelOptions
        {
            MaxDegreeOfParallelism = MaximumConcurrentHosts,
            CancellationToken = cancellationToken
        }, async (address, token) =>
        {
            var computer = await ProbeAddressAsync(address, localAddresses.Contains(address.ToString()), token).ConfigureAwait(false);
            computer = computer with { Device = await identification.IdentifyAsync(computer, connected, null, token).ConfigureAwait(false) };
            probed[computer.Address] = computer;
            if (computer.IsOnline) results[computer.Address] = computer;
            lock (progressGate)
            {
                completed++;
                progress?.Report(new LanDiscoveryProgress(completed, addresses.Length, computer.IsOnline ? computer : null));
            }
        });
        await Task.WhenAll(scanTask, metadataTask).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var observations = await metadataTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var address in observations.Addresses)
        {
            if (!IPAddress.TryParse(address, out var parsed) || !requested.Contains(ToUInt32(parsed)) || !actual.ContainsUsableHost(ToUInt32(parsed))) continue;
            if (probed.TryGetValue(address, out var computer))
                results[address] = computer with { IsOnline = true, ReachabilityEvidence = "Device advertisement received on this LAN", LastSeen = DateTimeOffset.UtcNow };
        }

        // Read the existing neighbor cache once, after probes have had a chance to resolve LAN neighbors.
        var neighbors = ReadNeighborCache();
        var identified = await Task.WhenAll(results.Values.Select(async computer =>
        {
            if (neighbors.TryGetValue(computer.Address, out var mac)) computer = computer with { MacAddress = mac };
            return computer with { Device = await identification.IdentifyAsync(computer, connected, observations, cancellationToken).ConfigureAwait(false) };
        })).ConfigureAwait(false);
        return identified
            .OrderBy(computer => computer.IsLocal)
            .ThenBy(computer => computer.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(computer => ToUInt32(IPAddress.Parse(computer.Address))).ToArray();
    }

    public async Task<LanComputer> ProbeHostAsync(string host, CancellationToken cancellationToken)
    {
        var normalized = NormalizeHost(host);
        cancellationToken.ThrowIfCancellationRequested();
        IPAddress[] resolved;
        if (IPAddress.TryParse(normalized, out var literal)) resolved = [literal];
        else
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(ManualResolveTimeoutMilliseconds);
                resolved = await Dns.GetHostAddressesAsync(normalized, AddressFamily.InterNetwork, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("Hostname lookup timed out. Try the computer's LAN IPv4 address.");
            }
            catch (SocketException)
            {
                throw new InvalidOperationException("The hostname could not be resolved. Try the computer's LAN IPv4 address.");
            }
        }
        var networks = GetNetworks();
        var allowed = resolved.Where(address => address.AddressFamily == AddressFamily.InterNetwork &&
                IsPrivateAddress(address) && networks.Any(network => ParseCidr(network.Cidr).ContainsUsableHost(ToUInt32(address))))
            .Distinct().Take(8).ToArray();
        if (allowed.Length == 0)
            throw new ArgumentException("Choose an IPv4 address or hostname on a directly connected private LAN.", nameof(host));

        foreach (var address in allowed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var local = networks.Any(network => network.LocalAddress == address.ToString());
            var result = await ProbeAddressAsync(address, local, cancellationToken).ConfigureAwait(false);
            var network = networks.First(item => ParseCidr(item.Cidr).ContainsUsableHost(ToUInt32(address)));
            var observations = await ObserveMetadataAsync(network, cancellationToken, address.ToString()).ConfigureAwait(false);
            if (!result.IsOnline && observations.ForAddress(result.Address).Count == 0) continue;
            if (observations.ForAddress(result.Address).Count > 0)
                result = result with { IsOnline = true, ReachabilityEvidence = "Device advertisement received on this LAN", LastSeen = DateTimeOffset.UtcNow };
            var neighbors = ReadNeighborCache();
            if (neighbors.TryGetValue(result.Address, out var mac)) result = result with { MacAddress = mac };
            return result with { Device = await new LanDeviceIdentificationService().IdentifyAsync(result, networks, observations, cancellationToken).ConfigureAwait(false) };
        }
        throw new InvalidOperationException("No response from this computer. Check its power, LAN connection, and remote-service firewall rules.");
    }

    public static bool IsLocalAddress(string address) => IPAddress.TryParse(address, out var parsed) &&
        (IPAddress.IsLoopback(parsed) || new LanDiscoveryService().GetNetworks().Any(network => network.LocalAddress == parsed.ToString()));

    public static bool IsAllowedLanAddress(string address) => IPAddress.TryParse(address, out var parsed) &&
        parsed.AddressFamily == AddressFamily.InterNetwork && IsPrivateAddress(parsed) &&
        new LanDiscoveryService().GetNetworks().Any(network => ParseCidr(network.Cidr).ContainsUsableHost(ToUInt32(parsed)));

    private static async Task<LanDeviceObservations> ObserveMetadataAsync(LanNetwork network, CancellationToken cancellationToken, string? targetAddress = null)
    {
        try { return await new LanDeviceMetadataDiscovery().DiscoverAsync(network, cancellationToken, targetAddress).ConfigureAwait(false); }
        catch (Exception exception) when (exception is SocketException or NetworkInformationException or System.IO.IOException or System.Net.Http.HttpRequestException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return LanDeviceObservations.Empty;
        }
    }

    private static async Task<LanComputer> ProbeAddressAsync(IPAddress address, bool isLocal, CancellationToken cancellationToken)
    {
        var ping = ProbePingAsync(address, cancellationToken);
        var smb = ProbeTcpAsync(address, 445, cancellationToken);
        var rdp = ProbeTcpAsync(address, 3389, cancellationToken);
        var http = ProbeTcpAsync(address, 5985, cancellationToken);
        var https = ProbeTcpAsync(address, 5986, cancellationToken);
        await Task.WhenAll(ping, smb, rdp, http, https).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var isOnline = isLocal || ping.Result || smb.Result.Reachable || rdp.Result.Reachable || http.Result.Reachable || https.Result.Reachable;
        var hostname = isLocal ? Environment.MachineName : isOnline ? await ResolveNameAsync(address, cancellationToken).ConfigureAwait(false) : string.Empty;
        var windowsCandidate = smb.Result.Available || rdp.Result.Available || http.Result.Available || https.Result.Available;
        return new LanComputer
        {
            Address = address.ToString(),
            HostName = hostname,
            IsLocal = isLocal,
            IsOnline = isOnline,
            PingAvailable = ping.Result,
            SmbAvailable = smb.Result.Available,
            RdpAvailable = rdp.Result.Available,
            WinRmHttpAvailable = http.Result.Available,
            WinRmHttpsAvailable = https.Result.Available,
            WindowsVerified = isLocal && OperatingSystem.IsWindows(),
            WindowsEvidence = isLocal && OperatingSystem.IsWindows() ? "This Windows computer" :
                windowsCandidate ? "Windows candidate · service ports detected; OS unverified" : "Operating system unknown",
            ReachabilityEvidence = isLocal ? "Local network adapter" : ping.Result ? "Ping reply" : isOnline ? "TCP connection or refusal received" : "No probe response",
            LastSeen = DateTimeOffset.UtcNow
        };
    }

    private static async Task<bool> ProbePingAsync(IPAddress address, CancellationToken cancellationToken)
    {
        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(ProbeTimeoutMilliseconds),
                Array.Empty<byte>(), null, cancellationToken).ConfigureAwait(false);
            return reply.Status == IPStatus.Success;
        }
        catch (PingException) { return false; }
        catch (SocketException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static async Task<PortProbe> ProbeTcpAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeoutMilliseconds);
        try
        {
            await client.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
            return new PortProbe(true, true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return default; }
        catch (SocketException exception)
        {
            // A refused connection still proves a target responded, without advertising a usable service.
            return new PortProbe(exception.SocketErrorCode == SocketError.ConnectionRefused, false);
        }
    }

    private static async Task<string> ResolveNameAsync(IPAddress address, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(NameTimeoutMilliseconds);
        try
        {
            var entry = await Dns.GetHostEntryAsync(address.ToString(), timeout.Token).ConfigureAwait(false);
            var hostname = entry.HostName;
            return hostname.Length <= 253 && !hostname.Any(char.IsControl) &&
                !string.Equals(hostname, address.ToString(), StringComparison.OrdinalIgnoreCase) ? hostname : string.Empty;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return string.Empty; }
        catch (SocketException) { return string.Empty; }
        catch (ArgumentException) { return string.Empty; }
    }

    private static string NormalizeHost(string host)
    {
        var normalized = host?.Trim() ?? string.Empty;
        if (normalized.Length == 0 || normalized.Length > 253 || normalized.Any(char.IsControl) ||
            normalized.IndexOfAny(['/', '\\', ':', '@', ' ', '\t', '?', '#']) >= 0)
            throw new ArgumentException("Enter a computer hostname or IPv4 address, without a URL, port, or path.", nameof(host));
        if (!IPAddress.TryParse(normalized, out _) && Uri.CheckHostName(normalized) != UriHostNameType.Dns)
            throw new ArgumentException("Enter a valid computer hostname or IPv4 address.", nameof(host));
        return normalized;
    }

    private static Ipv4Subnet ParseCidr(string cidr)
    {
        var parts = (cidr ?? string.Empty).Trim().Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) || address.AddressFamily != AddressFamily.InterNetwork ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix) || prefix is < 0 or > 32)
            throw new ArgumentException("Enter a valid IPv4 CIDR range, such as 192.168.1.0/24.", nameof(cidr));
        return new Ipv4Subnet(ToUInt32(address) & Mask(prefix), prefix);
    }

    private static IEnumerable<IPAddress> EnumerateHosts(Ipv4Subnet requested, Ipv4Subnet actual)
    {
        // Only the actual connected subnet reserves endpoints. A /24 scan slice inside a /16 LAN
        // must still discover valid hosts ending in .0 or .255. /31 and /32 have no reserved endpoint.
        for (var address = (ulong)requested.Network; address <= requested.LastAddress; address++)
            if (actual.ContainsUsableHost((uint)address)) yield return FromUInt32((uint)address);
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
            bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 169 && bytes[1] == 254;
    }

    private static int? PrefixFromMask(IPAddress mask)
    {
        if (mask.AddressFamily != AddressFamily.InterNetwork) return null;
        var bits = ToUInt32(mask);
        var prefix = 0;
        while ((bits & 0x80000000) != 0) { prefix++; bits <<= 1; }
        return bits == 0 ? prefix : null;
    }

    private static uint Mask(int prefix) => prefix == 0 ? 0U : uint.MaxValue << (32 - prefix);

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return (uint)bytes[0] << 24 | (uint)bytes[1] << 16 | (uint)bytes[2] << 8 | bytes[3];
    }

    private static IPAddress FromUInt32(uint address) => new(new byte[]
    {
        (byte)(address >> 24), (byte)(address >> 16), (byte)(address >> 8), (byte)address
    });

    private static Dictionary<string, string> ReadNeighborCache()
    {
        var neighbors = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!OperatingSystem.IsWindows()) return neighbors;
        const int errorInsufficientBuffer = 122;
        var size = 0;
        if (GetIpNetTable(IntPtr.Zero, ref size, false) != errorInsufficientBuffer || size < 4 || size > 1024 * 1024) return neighbors;
        var capacity = size;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetIpNetTable(buffer, ref size, false) != 0 || size < 4 || size > capacity) return neighbors;
            var count = Marshal.ReadInt32(buffer);
            if (count < 0 || count > (size - 4) / 24) return neighbors;
            for (var index = 0; index < count; index++)
            {
                var row = IntPtr.Add(buffer, 4 + index * 24);
                var length = Marshal.ReadInt32(row, 4);
                var type = Marshal.ReadInt32(row, 20);
                if (length is < 1 or > 8 || type is 1 or 2) continue;
                var mac = new byte[length];
                var address = new byte[4];
                Marshal.Copy(IntPtr.Add(row, 8), mac, 0, length);
                Marshal.Copy(IntPtr.Add(row, 16), address, 0, 4);
                neighbors[new IPAddress(address).ToString()] = string.Join(":", mac.Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));
            }
            return neighbors;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetIpNetTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order);

    private readonly record struct PortProbe(bool Reachable, bool Available);
    private readonly record struct Ipv4Subnet(uint Network, int Prefix)
    {
        public ulong AddressCount => 1UL << (32 - Prefix);
        public uint LastAddress => Network | ~Mask(Prefix);
        public bool Contains(uint address) => (address & Mask(Prefix)) == Network;
        public bool ContainsUsableHost(uint address) => Contains(address) &&
            (Prefix >= 31 || address != Network && address != LastAddress);
        public override string ToString() => $"{FromUInt32(Network)}/{Prefix}";
    }
}
