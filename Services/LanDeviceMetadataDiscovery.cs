using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using FullStackLauncher.Models;

namespace FullStackLauncher.Services;

/// <summary>
/// Reads standard mDNS/DNS-SD and SSDP advertisements on one selected IPv4 LAN.
/// No authentication, configuration changes, remote commands, or arbitrary web-page scans are performed.
/// </summary>
public sealed class LanDeviceMetadataDiscovery
{
    private const int DiscoveryMilliseconds = 2000;
    private const int MaximumPackets = 128;
    private const int MaximumRecordsPerPacket = 128;
    private const int MaximumDnsQueries = 32;
    private const int MaximumDescriptions = 16;
    private const int MaximumPacketBytes = 9000;
    private const int MaximumDescriptionBytes = 64 * 1024;
    private static readonly IPEndPoint MdnsEndpoint = new(IPAddress.Parse("224.0.0.251"), 5353);
    private static readonly IPEndPoint SsdpEndpoint = new(IPAddress.Parse("239.255.255.250"), 1900);
    private static readonly string[] BrowseServices =
    [
        "_services._dns-sd._udp.local", "_device-info._tcp.local", "_googlecast._tcp.local",
        "_airplay._tcp.local", "_raop._tcp.local", "_adb-tls-connect._tcp.local",
        "_adb-tls-pairing._tcp.local", "_androidtvremote2._tcp.local", "_ipp._tcp.local",
        "_ipps._tcp.local", "_printer._tcp.local"
    ];

    public async Task<LanDeviceObservations> DiscoverAsync(
        LanNetwork network, CancellationToken cancellationToken, string? targetAddress = null)
    {
        ArgumentNullException.ThrowIfNull(network);
        cancellationToken.ThrowIfCancellationRequested();
        var current = new LanDiscoveryService().GetNetworks().FirstOrDefault(item =>
            item.InterfaceId == network.InterfaceId && item.LocalAddress == network.LocalAddress && item.Cidr == network.Cidr);
        if (current is null || !LanScope.TryCreate(current, targetAddress, out var scope)) return LanDeviceObservations.Empty;

        var mdnsTask = DiscoverMdnsAsync(scope, cancellationToken);
        var ssdpTask = DiscoverSsdpAsync(scope, cancellationToken);
        await Task.WhenAll(mdnsTask, ssdpTask).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new LanDeviceObservations(mdnsTask.Result.Concat(ssdpTask.Result)
            .DistinctBy(item => (item.Address, item.Protocol, item.ServiceType, item.Name)).Take(512).ToArray());
    }

    private static UdpClient CreateSocket(LanScope scope, int ttl)
    {
        var socket = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            socket.Client.Bind(new IPEndPoint(scope.LocalAddress, 0));
            socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                scope.LocalAddress.GetAddressBytes());
            socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, ttl);
            socket.MulticastLoopback = false;
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<IReadOnlyList<LanDeviceAdvertisement>> DiscoverMdnsAsync(
        LanScope scope, CancellationToken cancellationToken)
    {
        var records = new List<DnsObservation>(512);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DiscoveryMilliseconds);
        try
        {
            // An ephemeral source port requests legacy unicast replies. This avoids sharing port 5353
            // with Windows Bonjour/mDNS services or installing a long-lived multicast listener.
            using var socket = CreateSocket(scope, 255);
            var transaction = (ushort)RandomNumberGenerator.GetInt32(1, ushort.MaxValue + 1);
            var queries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            async Task QueryAsync(string name, ushort type)
            {
                if (queries.Count >= MaximumDnsQueries || !queries.Add($"{type}:{name}")) return;
                var packet = BuildDnsQuery(name, type, transaction);
                if (packet.Length > 0) await socket.SendAsync(packet, MdnsEndpoint, timeout.Token).ConfigureAwait(false);
            }
            foreach (var service in BrowseServices) await QueryAsync(service, 12).ConfigureAwait(false);
            for (var index = 0; index < MaximumPackets && records.Count < 2048; index++)
            {
                var response = await socket.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                if (response.Buffer.Length is < 12 or > MaximumPacketBytes || response.RemoteEndPoint.Port != 5353 ||
                    !scope.Allows(response.RemoteEndPoint.Address) ||
                    BinaryPrimitives.ReadUInt16BigEndian(response.Buffer) != transaction) continue;
                var parsed = ParseDnsResponse(response.Buffer, response.RemoteEndPoint.Address.ToString());
                records.AddRange(parsed);
                foreach (var record in parsed)
                {
                    if (record.Type == 12 && record.Owner.Equals(BrowseServices[0], StringComparison.OrdinalIgnoreCase) &&
                        IsServiceName(record.Target)) await QueryAsync(record.Target, 12).ConfigureAwait(false);
                    else if (record.Type == 12 && IsServiceName(record.Owner) && record.Target.Length > record.Owner.Length &&
                        record.Target.EndsWith('.' + record.Owner, StringComparison.OrdinalIgnoreCase))
                    {
                        await QueryAsync(record.Target, 33).ConfigureAwait(false);
                        await QueryAsync(record.Target, 16).ConfigureAwait(false);
                    }
                    else if (record.Type == 33 && record.Target.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
                        await QueryAsync(record.Target, 1).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (SocketException) { }
        catch (PlatformNotSupportedException) { }
        catch (InvalidOperationException) { }
        cancellationToken.ThrowIfCancellationRequested();
        return BuildMdnsAdvertisements(records);
    }

    private static byte[] BuildDnsQuery(string name, ushort type, ushort transaction)
    {
        using var packet = new MemoryStream();
        packet.WriteByte((byte)(transaction >> 8));
        packet.WriteByte((byte)transaction);
        packet.Write(new byte[] { 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
        foreach (var label in name.Split('.'))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            if (bytes.Length is < 1 or > 63) return Array.Empty<byte>();
            packet.WriteByte((byte)bytes.Length);
            packet.Write(bytes);
        }
        packet.WriteByte(0);
        packet.Write(new byte[] { (byte)(type >> 8), (byte)type, 0, 1 });
        return packet.Length <= 512 ? packet.ToArray() : Array.Empty<byte>();
    }

    private static IReadOnlyList<DnsObservation> ParseDnsResponse(byte[] packet, string sender)
    {
        var records = new List<DnsObservation>();
        if ((packet[2] & 0x80) == 0 || (packet[3] & 0x0f) != 0) return records;
        var questions = ReadU16(packet, 4);
        var count = ReadU16(packet, 6) + ReadU16(packet, 8) + ReadU16(packet, 10);
        if (questions > 32 || count > MaximumRecordsPerPacket) return records;
        var position = 12;
        for (var index = 0; index < questions; index++)
        {
            if (!TryReadDnsName(packet, ref position, out _) || position > packet.Length - 4) return records;
            position += 4;
        }
        for (var index = 0; index < count; index++)
        {
            if (!TryReadDnsName(packet, ref position, out var owner) || position > packet.Length - 10) break;
            var type = ReadU16(packet, position);
            var recordClass = ReadU16(packet, position + 2) & 0x7fff;
            var ttl = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(position + 4, 4));
            var length = ReadU16(packet, position + 8);
            position += 10;
            if (position > packet.Length - length) break;
            var end = position + length;
            if (recordClass == 1 && ttl > 0)
            {
                var dataPosition = position;
                var target = string.Empty;
                var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var valid = false;
                if (type == 1 && length == 4)
                {
                    target = new IPAddress(packet.AsSpan(position, 4)).ToString();
                    valid = true;
                }
                else if (type == 12) valid = TryReadDnsName(packet, ref dataPosition, out target) && dataPosition <= end;
                else if (type == 33 && length >= 7)
                {
                    properties["port"] = ReadU16(packet, position + 4).ToString(CultureInfo.InvariantCulture);
                    dataPosition += 6;
                    valid = TryReadDnsName(packet, ref dataPosition, out target) && dataPosition <= end;
                }
                else if (type == 16)
                {
                    valid = true;
                    for (var item = 0; dataPosition < end && item < 64; item++)
                    {
                        var size = packet[dataPosition++];
                        if (dataPosition > end - size) { valid = false; break; }
                        var text = Clean(Encoding.UTF8.GetString(packet, dataPosition, size));
                        dataPosition += size;
                        var equals = text.IndexOf('=');
                        var key = equals < 0 ? text : text[..equals];
                        if (key.Length is > 0 and <= 64 && !properties.ContainsKey(key))
                            properties[key] = equals < 0 ? string.Empty : text[(equals + 1)..];
                    }
                }
                if (valid && owner.Length > 0) records.Add(new DnsObservation(sender, type, owner, target, properties));
            }
            position = end;
        }
        return records;
    }

    private static bool TryReadDnsName(byte[] packet, ref int position, out string name)
    {
        name = string.Empty;
        var labels = new List<string>();
        var seen = new HashSet<int>();
        var cursor = position;
        var continuation = -1;
        var totalLength = 0;
        for (var count = 0; count < 128; count++)
        {
            if (cursor < 12 || cursor >= packet.Length || !seen.Add(cursor)) return false;
            var size = packet[cursor++];
            if ((size & 0xc0) == 0xc0)
            {
                if (cursor >= packet.Length) return false;
                var pointer = ((size & 0x3f) << 8) | packet[cursor++];
                if (continuation < 0) continuation = cursor;
                cursor = pointer;
                continue;
            }
            if ((size & 0xc0) != 0 || size > 63) return false;
            if (size == 0)
            {
                position = continuation < 0 ? cursor : continuation;
                name = string.Join('.', labels);
                return true;
            }
            if (cursor > packet.Length - size || (totalLength += size + 1) > 255) return false;
            var label = Encoding.UTF8.GetString(packet, cursor, size);
            if (label.Any(char.IsControl)) return false;
            labels.Add(label);
            cursor += size;
        }
        return false;
    }

    private static IReadOnlyList<LanDeviceAdvertisement> BuildMdnsAdvertisements(List<DnsObservation> records)
    {
        var advertisements = new List<LanDeviceAdvertisement>();
        foreach (var senderGroup in records.GroupBy(record => record.Sender))
        {
            var senderRecords = senderGroup.ToArray();
            var addressRecords = senderRecords.Where(record => record.Type == 1).ToArray();
            var instances = senderRecords.Where(record => record.Type is 16 or 33 &&
                    !string.IsNullOrEmpty(ServiceFromInstance(record.Owner)))
                .Select(record => record.Owner).Distinct(StringComparer.OrdinalIgnoreCase).Take(32);
            foreach (var instance in instances)
            {
                var service = ServiceFromInstance(instance);
                var srv = senderRecords.FirstOrDefault(record => record.Type == 33 &&
                    record.Owner.Equals(instance, StringComparison.OrdinalIgnoreCase));
                var target = srv?.Target ?? string.Empty;
                var targetAddresses = addressRecords.Where(record => record.Owner.Equals(target, StringComparison.OrdinalIgnoreCase)).ToArray();
                // Never attribute records for a different advertised address to the sender.
                if (targetAddresses.Length > 0 && targetAddresses.Any(record => record.Target != senderGroup.Key)) continue;
                var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var record in senderRecords.Where(record => record.Owner.Equals(instance, StringComparison.OrdinalIgnoreCase)))
                    foreach (var property in record.Properties) properties.TryAdd(property.Key, property.Value);
                var instanceName = instance[..^(service.Length + 1)];
                advertisements.Add(new LanDeviceAdvertisement
                {
                    Address = senderGroup.Key,
                    Protocol = "mDNS",
                    ServiceType = service,
                    Name = Clean(instanceName),
                    HostName = Clean(target),
                    FriendlyName = Property(properties, "fn", "name", "ty"),
                    Manufacturer = Property(properties, "manufacturer", "mf", "mfg", "usb_MFG"),
                    Model = Property(properties, "model", "md", "product", "usb_MDL"),
                    DeviceType = Property(properties, "type", "deviceType"),
                    Properties = properties
                });
            }
            // An A response still provides a useful local hostname even when the device publishes no services.
            foreach (var record in addressRecords.Where(record => record.Target == senderGroup.Key).Take(8))
                if (!advertisements.Any(item => item.Address == senderGroup.Key && item.HostName.Equals(record.Owner, StringComparison.OrdinalIgnoreCase)))
                    advertisements.Add(new LanDeviceAdvertisement { Address = senderGroup.Key, Protocol = "mDNS", HostName = Clean(record.Owner) });
        }
        return advertisements;
    }

    private static string ServiceFromInstance(string instance)
    {
        var start = instance.IndexOf("._", StringComparison.Ordinal);
        while (start >= 0)
        {
            var service = instance[(start + 1)..];
            if (IsServiceName(service)) return service;
            start = instance.IndexOf("._", start + 2, StringComparison.Ordinal);
        }
        return string.Empty;
    }

    private static bool IsServiceName(string name) => name.Length is > 11 and <= 253 && name[0] == '_' &&
        (name.EndsWith("._tcp.local", StringComparison.OrdinalIgnoreCase) || name.EndsWith("._udp.local", StringComparison.OrdinalIgnoreCase)) &&
        name.Split('.').Length == 3 && !name.Any(char.IsControl);

    private static async Task<IReadOnlyList<LanDeviceAdvertisement>> DiscoverSsdpAsync(
        LanScope scope, CancellationToken cancellationToken)
    {
        var observations = new List<SsdpObservation>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DiscoveryMilliseconds);
        try
        {
            using var socket = CreateSocket(scope, 1);
            var query = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 1\r\nST: ssdp:all\r\n\r\n");
            await socket.SendAsync(query, SsdpEndpoint, timeout.Token).ConfigureAwait(false);
            for (var index = 0; index < MaximumPackets; index++)
            {
                var response = await socket.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                if (response.Buffer.Length > MaximumPacketBytes || !scope.Allows(response.RemoteEndPoint.Address)) continue;
                var headers = ParseSsdpHeaders(response.Buffer);
                if (headers is null) continue;
                var sender = response.RemoteEndPoint.Address;
                var service = Property(headers, "ST");
                var usn = Property(headers, "USN");
                if (service.Length == 0 && usn.Length == 0) continue;
                if (observations.Any(item => item.Address.Equals(sender) && item.Service == service && item.Usn == usn)) continue;
                headers.TryGetValue("LOCATION", out var location);
                observations.Add(new SsdpObservation(sender, service, usn, location ?? string.Empty, headers));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (SocketException) { }
        catch (PlatformNotSupportedException) { }
        catch (InvalidOperationException) { }
        cancellationToken.ThrowIfCancellationRequested();

        // Description requests are bounded globally, not repeated for every advertised service.
        var descriptions = observations.Where(item => !string.IsNullOrEmpty(item.Location))
            .DistinctBy(item => (item.Address.ToString(), item.Location)).Take(MaximumDescriptions).ToArray();
        var fetched = await Task.WhenAll(descriptions.Select(item =>
            ReadDescriptionAsync(scope, item, cancellationToken))).ConfigureAwait(false);
        var details = descriptions.Select((item, index) => (item, description: fetched[index]))
            .ToDictionary(item => (item.item.Address.ToString(), item.item.Location), item => item.description);
        return observations.Select(item =>
        {
            details.TryGetValue((item.Address.ToString(), item.Location), out var description);
            // LOCATION is consumed internally; never expose arbitrary advertised URLs as actionable UI links.
            var properties = item.Headers.Where(header => header.Key.Equals("SERVER", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(header => header.Key, header => header.Value, StringComparer.OrdinalIgnoreCase);
            return new LanDeviceAdvertisement
            {
                Address = item.Address.ToString(), Protocol = "SSDP", ServiceType = item.Service, Name = item.Usn,
                FriendlyName = description?.FriendlyName ?? string.Empty,
                Manufacturer = description?.Manufacturer ?? string.Empty, Model = description?.Model ?? string.Empty,
                DeviceType = description?.DeviceType ?? string.Empty, Properties = properties
            };
        }).ToArray();
    }

    private static Dictionary<string, string>? ParseSsdpHeaders(byte[] packet)
    {
        var lines = Encoding.UTF8.GetString(packet).Split('\n');
        if (lines.Length == 0 || !lines[0].TrimEnd('\r').Equals("HTTP/1.1 200 OK", StringComparison.OrdinalIgnoreCase)) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1).Take(64))
        {
            if (string.IsNullOrWhiteSpace(line)) break;
            var colon = line.IndexOf(':');
            if (colon is < 1 or > 64) continue;
            var key = line[..colon].Trim();
            var raw = line[(colon + 1)..].Trim();
            if (!key.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '.')) continue;
            if (raw.Length <= 2048 && !raw.Any(char.IsControl)) headers.TryAdd(key, raw);
        }
        return headers;
    }

    private static async Task<DeviceDescription?> ReadDescriptionAsync(
        LanScope scope, SsdpObservation observation, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(observation.Location, UriKind.Absolute, out var location) ||
            location.Scheme != Uri.UriSchemeHttp || !string.IsNullOrEmpty(location.UserInfo) ||
            !string.IsNullOrEmpty(location.Fragment) || location.Port is < 1 or > 65535) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(1000);
        try
        {
            if (IPAddress.TryParse(location.DnsSafeHost, out var literal))
            {
                if (!literal.Equals(observation.Address) || !scope.Allows(literal)) return null;
            }
            else
            {
                if (Uri.CheckHostName(location.DnsSafeHost) != UriHostNameType.Dns) return null;
                var addresses = await Dns.GetHostAddressesAsync(location.DnsSafeHost, timeout.Token).ConfigureAwait(false);
                // All answers must identify exactly the sender; mixed IPv4/IPv6 or off-LAN responses are rejected.
                if (addresses.Length == 0 || addresses.Any(address => !address.Equals(observation.Address))) return null;
            }
            using var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false, UseCookies = false, UseProxy = false, Credentials = null,
                AutomaticDecompression = DecompressionMethods.None, MaxResponseHeadersLength = 8,
                ConnectTimeout = TimeSpan.FromMilliseconds(750),
                ConnectCallback = async (context, token) =>
                {
                    if (context.DnsEndPoint.Port != location.Port ||
                        !context.DnsEndPoint.Host.Equals(location.DnsSafeHost, StringComparison.OrdinalIgnoreCase))
                        throw new HttpRequestException("Unexpected device-description endpoint.");
                    var connection = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        // Pin the verified packet sender to prevent a second DNS lookup or DNS rebinding.
                        connection.Bind(new IPEndPoint(scope.LocalAddress, 0));
                        await connection.ConnectAsync(new IPEndPoint(observation.Address, location.Port), token).ConfigureAwait(false);
                        return new NetworkStream(connection, ownsSocket: true);
                    }
                    catch { connection.Dispose(); throw; }
                }
            };
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            using var request = new HttpRequestMessage(HttpMethod.Get, location)
            {
                Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumDescriptionBytes ||
                response.Content.Headers.ContentEncoding.Count > 0) return null;
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var block = new byte[4096];
            while (true)
            {
                var read = await input.ReadAsync(block, timeout.Token).ConfigureAwait(false);
                if (read == 0) break;
                if (buffer.Length + read > MaximumDescriptionBytes) return null;
                buffer.Write(block, 0, read);
            }
            buffer.Position = 0;
            var xmlSettings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = MaximumDescriptionBytes, IgnoreComments = true, IgnoreProcessingInstructions = true
            };
            // Limit nesting and element counts before materializing untrusted XML.
            using (var validation = XmlReader.Create(buffer, xmlSettings))
            {
                var nodes = 0;
                while (validation.Read())
                    if (validation.Depth > 32 || ++nodes > 4096) return null;
            }
            buffer.Position = 0;
            using var reader = XmlReader.Create(buffer, xmlSettings);
            var document = XDocument.Load(reader, LoadOptions.None);
            var root = document.Root;
            if (root is null || root.Name.LocalName != "root") return null;
            var device = root.Elements().FirstOrDefault(element => element.Name.LocalName == "device");
            if (device is null) return null;
            string Value(string field)
            {
                var element = device.Elements().FirstOrDefault(element => element.Name.LocalName == field);
                return element is null || element.HasElements ? string.Empty : Clean(element.Value);
            }
            return new DeviceDescription(Value("friendlyName"), Value("manufacturer"), Value("modelName"), Value("deviceType"));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (HttpRequestException) { return null; }
        catch (SocketException) { return null; }
        catch (IOException) { return null; }
        catch (XmlException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (ArgumentException) { return null; }
    }

    private static string Property(IReadOnlyDictionary<string, string> properties, params string[] keys)
    {
        foreach (var key in keys) if (properties.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) return Clean(value);
        return string.Empty;
    }

    private static string Clean(string? value) => string.IsNullOrEmpty(value) ? string.Empty :
        new string(value.Where(character => !char.IsControl(character)).Take(256).ToArray()).Trim();
    private static ushort ReadU16(byte[] packet, int offset) => BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset, 2));
    private sealed record DnsObservation(string Sender, ushort Type, string Owner, string Target, Dictionary<string, string> Properties);
    private sealed record SsdpObservation(IPAddress Address, string Service, string Usn, string Location, Dictionary<string, string> Headers);
    private sealed record DeviceDescription(string FriendlyName, string Manufacturer, string Model, string DeviceType);

    private sealed record LanScope(IPAddress LocalAddress, uint Network, uint Mask, uint LastAddress, int Prefix, IPAddress? Target)
    {
        public bool Allows(IPAddress address)
        {
            if (!IsPrivate(address)) return false;
            var number = ToUInt32(address);
            return (number & Mask) == Network && (Prefix >= 31 || number != Network && number != LastAddress) &&
                (Target is null || Target.Equals(address));
        }

        public static bool TryCreate(LanNetwork network, string? targetAddress, out LanScope scope)
        {
            scope = null!;
            var parts = network.Cidr.Split('/');
            if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) || !IsPrivate(address) ||
                !IPAddress.TryParse(network.LocalAddress, out var local) || !IsPrivate(local) ||
                !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix) || prefix is < 0 or > 32) return false;
            var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
            var start = ToUInt32(address) & mask;
            var end = start | ~mask;
            if ((ToUInt32(local) & mask) != start || !IsPrivate(FromUInt32(start)) || !IsPrivate(FromUInt32(end))) return false;
            IPAddress? target = null;
            if (targetAddress is not null && !IPAddress.TryParse(targetAddress, out target)) return false;
            scope = new LanScope(local, start, mask, end, prefix, target);
            return target is null || scope.Allows(target);
        }

        private static bool IsPrivate(IPAddress address)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork) return false;
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 169 && bytes[1] == 254;
        }
        private static uint ToUInt32(IPAddress address)
        {
            var bytes = address.GetAddressBytes();
            return (uint)bytes[0] << 24 | (uint)bytes[1] << 16 | (uint)bytes[2] << 8 | bytes[3];
        }
        private static IPAddress FromUInt32(uint address) => new(new byte[]
            { (byte)(address >> 24), (byte)(address >> 16), (byte)(address >> 8), (byte)address });
    }
}
