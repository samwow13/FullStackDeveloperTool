using System.Text.RegularExpressions;
using FullStackLauncher.Models;

namespace FullStackLauncher.Services;

/// <summary>
/// Combines bounded network advertisements with reachability and adapter observations. No network
/// metadata is uploaded, credentials are not used, and service ports alone never verify an OS.
/// </summary>
public sealed class LanDeviceIdentificationService
{
    private const int MaximumAdvertisements = 32;
    private const int MaximumTextLength = 200;

    public Task<LanDeviceIdentity> IdentifyAsync(LanComputer computer, IReadOnlyList<LanNetwork> networks,
        LanDeviceObservations? observations, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(computer);
        ArgumentNullException.ThrowIfNull(networks);
        cancellationToken.ThrowIfCancellationRequested();
        var advertisements = observations?.ForAddress(computer.Address).Take(MaximumAdvertisements).ToArray() ?? [];
        var evidence = new List<string>();
        var services = new List<string>();
        var friendlyName = First(advertisements.Select(advertisement => advertisement.FriendlyName));
        if (friendlyName.Length == 0)
            friendlyName = First(advertisements.Select(advertisement => advertisement.HostName)).TrimEnd('.');
        var manufacturer = First(advertisements.Select(advertisement => advertisement.Manufacturer));
        var model = First(advertisements.Select(advertisement => advertisement.Model));
        var classification = ClassifyAdvertisements(advertisements);

        if (computer.IsLocal && computer.WindowsVerified)
        {
            classification = new Classification("Windows PC", "Verified", "This computer is running Windows.");
            friendlyName = string.IsNullOrWhiteSpace(computer.HostName) ? Environment.MachineName : Clean(computer.HostName);
        }
        else if (networks.Any(network => network.GatewayAddress == computer.Address))
            classification = new Classification("Router / gateway", "Observed role", "Configured default gateway on this LAN adapter.");
        else if (classification is null)
        {
            classification = ClassifyName(Clean(computer.HostName), false);
            if (classification is null && (computer.WinRmHttpAvailable || computer.WinRmHttpsAvailable || computer.RdpAvailable))
                classification = new Classification("Windows PC candidate", "Possible", "Windows remote-service ports respond; operating system is unverified.");
            if (classification is null && computer.SmbAvailable)
                classification = new Classification("Computer / NAS candidate", "Possible", "File-sharing port responds; Windows, macOS, Linux, and NAS devices can provide SMB.");
        }

        if (classification is not null) evidence.Add(classification.Evidence);
        if (!string.IsNullOrWhiteSpace(computer.HostName)) evidence.Add($"Hostname: {Clean(computer.HostName)}");
        if (computer.PingAvailable) services.Add("ICMP ping");
        AddPort(computer.SmbAvailable, "File sharing", 445, services, evidence);
        AddPort(computer.RdpAvailable, "Remote Desktop", 3389, services, evidence);
        AddPort(computer.WinRmHttpAvailable, "WinRM HTTP", 5985, services, evidence);
        AddPort(computer.WinRmHttpsAvailable, "WinRM HTTPS", 5986, services, evidence);

        foreach (var advertisement in advertisements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var protocol = Clean(advertisement.Protocol);
            var serviceType = Clean(advertisement.ServiceType);
            if (serviceType.Length > 0)
            {
                services.Add(ServiceLabel(serviceType));
                evidence.Add($"{protocol} advertises {serviceType}");
            }
            var reportedName = Clean(advertisement.FriendlyName);
            var reportedModel = Clean(advertisement.Model);
            var reportedManufacturer = Clean(advertisement.Manufacturer);
            if (reportedName.Length > 0) evidence.Add($"{protocol} name: {reportedName}");
            if (!string.IsNullOrWhiteSpace(advertisement.HostName))
                evidence.Add($"{protocol} hostname: {Clean(advertisement.HostName)}");
            if (reportedManufacturer.Length > 0) evidence.Add($"{protocol} manufacturer: {reportedManufacturer}");
            if (reportedModel.Length > 0) evidence.Add($"{protocol} model: {reportedModel}");
            if (!string.IsNullOrWhiteSpace(advertisement.DeviceType))
                evidence.Add($"{protocol} device type: {Clean(advertisement.DeviceType)}");
        }

        // A locally administered address includes private/randomized MACs, virtual NICs and manual
        // assignments. Its bit never implies a particular device maker or operating system.
        if (IsLocallyAdministeredMac(computer.MacAddress))
            evidence.Add("MAC uses a locally administered address; it may be private, randomized, or assigned manually.");
        if (classification is null)
            evidence.Add("Observed information does not identify the device type.");

        manufacturer = manufacturer.Length > 0 ? manufacturer : InferManufacturer(model);
        return Task.FromResult(new LanDeviceIdentity
        {
            DeviceType = classification?.DeviceType ?? "Unknown device",
            Confidence = classification?.Confidence ?? "Unknown",
            Manufacturer = manufacturer,
            Model = model,
            FriendlyName = friendlyName,
            Evidence = evidence.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Services = services.Where(service => service.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        });
    }

    private static Classification? ClassifyAdvertisements(IReadOnlyList<LanDeviceAdvertisement> advertisements)
    {
        // Prefer device-specific model information over generic services shared by multiple platforms.
        foreach (var advertisement in advertisements)
        {
            var model = Clean(advertisement.Model);
            var classification = ClassifyName(model, true);
            if (classification is not null) return classification;
        }
        foreach (var advertisement in advertisements)
        {
            var deviceType = Clean(advertisement.DeviceType);
            if (Contains(deviceType, "InternetGatewayDevice", "WANDevice", "WANConnectionDevice"))
                return Advertised("Router / gateway", "Advertised UPnP gateway device type.");
            if (Contains(deviceType, "Printer", "Scanner"))
                return Advertised("Printer / scanner", "Advertised printer or scanner device type.");
        }
        var serviceTypes = string.Join(" ", advertisements.Select(advertisement => advertisement.ServiceType));
        if (Contains(serviceTypes, "_androidtvremote", "_androidtvremote2"))
            return Advertised("Android TV", "Advertised Android TV remote service.");
        if (Contains(serviceTypes, "_ipp.", "_ipps.", "_printer.", "_pdl-datastream.", "_scanner.", "_uscan."))
            return Advertised("Printer / scanner", "Advertised printing or scanning service.");
        if (Contains(serviceTypes, "_adb-tls-connect.", "_adb-tls-pairing.", "_adb._tcp"))
            return Advertised("Android device", "Advertised Android Debug Bridge service; device could be a phone, tablet, TV, or emulator.");
        if (Contains(serviceTypes, "_googlecast."))
            return Advertised("Google Cast device", "Advertised Google Cast receiver service; phone operating system is not implied.");
        if (Contains(serviceTypes, "_airplay.", "_raop."))
            return Advertised("AirPlay receiver", "Advertised AirPlay or network audio receiver; Apple manufacture is not implied.");

        foreach (var advertisement in advertisements)
        {
            var classification = ClassifyName(Clean(advertisement.FriendlyName), false)
                ?? ClassifyName(Clean(advertisement.HostName), false)
                ?? ClassifyName(Clean(advertisement.Name), false);
            if (classification is not null) return classification;
        }
        if (Contains(serviceTypes, "_smb.", "_afpovertcp.", "_nfs.", "_ssh.", "_sftp-ssh."))
            return Advertised("Computer / NAS candidate", "Advertised file-sharing or remote-shell service; operating system is unverified.");
        if (Contains(serviceTypes, "MediaRenderer", "MediaServer", "_dial."))
            return Advertised("Media device", "Advertised media playback or server service.");
        return null;
    }

    private static Classification? ClassifyName(string name, bool isModel)
    {
        if (name.Length == 0) return null;
        string? deviceType = null;
        if (Contains(name, "iPhone")) deviceType = "iPhone";
        else if (Contains(name, "iPad")) deviceType = "iPad";
        else if (Contains(name, "MacBook", "Macmini", "Mac mini", "MacPro", "Mac Pro", "MacStudio", "Mac Studio", "iMac"))
            deviceType = "Mac computer";
        else if (Contains(name, "AppleTV", "Apple TV")) deviceType = "Apple TV";
        else if (Contains(name, "HomePod")) deviceType = "Apple speaker";
        else if (Contains(name, "Galaxy Tab", "Pixel Tablet", "OnePlus Pad", "Redmi Pad", "Xiaomi Pad") || Regex.IsMatch(name, @"\bSM-T[0-9]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            deviceType = "Android tablet";
        else if (Regex.IsMatch(name, @"\b(?:Google[ -])?Pixel[ -]?(?:[2-9]|1[0-9])(?:[a-z0-9 -]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
                 Regex.IsMatch(name, @"\bGalaxy[ -](?:S|A|M|F|Note|Z)[ -]?(?:[0-9]|Fold|Flip)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
                 Regex.IsMatch(name, @"\bSM-[GSAFM][0-9]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
                 Regex.IsMatch(name, @"\bOnePlus[ -](?:[0-9]|Nord)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
                 Regex.IsMatch(name, @"\b(?:Moto[ -](?:G|E|Edge|Razr)|Redmi[ -](?:Note|[0-9]))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            deviceType = "Android phone";
        else if (Contains(name, "Android TV", "BRAVIA", "SHIELD Android TV", "SHIELD TV", "Google TV"))
            deviceType = "TV / streaming device";
        else if (Contains(name, "Chromecast")) deviceType = "Chromecast";
        else if (Contains(name, "Nest Audio", "Nest Mini", "Nest Hub", "Google Home")) deviceType = "Speaker / display";
        else if (Contains(name, "Roku", "Fire TV", "FireTV")) deviceType = "TV / streaming device";
        else if (Contains(name, "PlayStation", "Xbox", "Nintendo Switch")) deviceType = "Game console";
        else if (Contains(name, "DiskStation", "RackStation", "Synology", "QNAP", "ReadyNAS", "My Cloud")) deviceType = "NAS";
        else if (Contains(name, "LaserJet", "OfficeJet", "DeskJet", "EcoTank", "PIXMA", "WorkCentre", "imageCLASS")) deviceType = "Printer";
        else if (HasWord(name, "android")) deviceType = "Android device";
        else if (HasWord(name, "router")) deviceType = "Router candidate";
        else if (name.StartsWith("DESKTOP-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LAPTOP-", StringComparison.OrdinalIgnoreCase))
            deviceType = "Windows PC candidate";
        if (deviceType is null) return null;
        return new Classification(deviceType, isModel ? "Likely" : "Possible",
            $"{(isModel ? "Advertised model" : "Device name")} suggests {deviceType}: {name}");
    }

    private static string ServiceLabel(string serviceType)
    {
        if (Contains(serviceType, "_googlecast.")) return "Google Cast";
        if (Contains(serviceType, "_androidtvremote")) return "Android TV remote";
        if (Contains(serviceType, "_adb-tls-pairing.")) return "Android wireless debugging pairing";
        if (Contains(serviceType, "_adb-tls-connect.", "_adb._tcp")) return "Android wireless debugging";
        if (Contains(serviceType, "_airplay.")) return "AirPlay";
        if (Contains(serviceType, "_raop.")) return "Network audio";
        if (Contains(serviceType, "_ipp.", "_ipps.", "_printer.", "_pdl-datastream.")) return "Network printing";
        if (Contains(serviceType, "_scanner.", "_uscan.")) return "Network scanning";
        if (Contains(serviceType, "_smb.")) return "File sharing (advertised)";
        if (Contains(serviceType, "_ssh.", "_sftp-ssh.")) return "SSH (advertised)";
        if (Contains(serviceType, "_device-info.")) return "Device information";
        if (Contains(serviceType, "_companion-link.")) return "Apple companion service";
        if (Contains(serviceType, "_http.", "_https.")) return "Web service (advertised)";
        if (Contains(serviceType, "InternetGatewayDevice", "WANDevice", "WANConnectionDevice")) return "UPnP gateway";
        if (Contains(serviceType, "MediaRenderer")) return "UPnP media receiver";
        if (Contains(serviceType, "MediaServer")) return "UPnP media server";
        if (Contains(serviceType, "_dial.")) return "DIAL media service";
        return serviceType;
    }

    private static string InferManufacturer(string model)
    {
        // Manufacturer inference uses identifiable model families only. Service compatibility (for
        // example AirPlay or Cast) and randomized MAC addresses do not identify the hardware maker.
        if (Contains(model, "iPhone", "iPad", "MacBook", "Macmini", "Mac mini", "MacPro", "Mac Pro", "MacStudio", "Mac Studio", "iMac", "AppleTV", "Apple TV", "HomePod")) return "Apple (model hint)";
        if (Contains(model, "Galaxy") || Regex.IsMatch(model, @"\bSM-[GSAFMT][0-9]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "Samsung (model hint)";
        if (Contains(model, "Pixel", "Chromecast", "Nest Audio", "Nest Mini", "Nest Hub", "Google Home")) return "Google (model hint)";
        if (Contains(model, "OnePlus")) return "OnePlus (model hint)";
        if (Contains(model, "Redmi", "Xiaomi")) return "Xiaomi (model hint)";
        if (Regex.IsMatch(model, @"\bMoto[ -](?:G|E|Edge|Razr)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "Motorola (model hint)";
        if (Contains(model, "DiskStation", "RackStation", "Synology")) return "Synology (model hint)";
        if (Contains(model, "QNAP")) return "QNAP (model hint)";
        if (Contains(model, "LaserJet", "OfficeJet", "DeskJet")) return "HP (model hint)";
        if (Contains(model, "EcoTank")) return "Epson (model hint)";
        if (Contains(model, "PIXMA", "imageCLASS")) return "Canon (model hint)";
        if (Contains(model, "WorkCentre")) return "Xerox (model hint)";
        if (Contains(model, "Xbox")) return "Microsoft (model hint)";
        if (Contains(model, "PlayStation", "BRAVIA")) return "Sony (model hint)";
        if (Contains(model, "Nintendo Switch")) return "Nintendo (model hint)";
        return string.Empty;
    }

    private static void AddPort(bool available, string label, int port, List<string> services, List<string> evidence)
    {
        if (!available) return;
        services.Add(label);
        evidence.Add($"TCP {port} accepts connections ({label}).");
    }

    private static bool IsLocallyAdministeredMac(string mac)
    {
        var firstOctet = mac.Split(':', '-').FirstOrDefault();
        return byte.TryParse(firstOctet, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var first) && (first & 2) != 0;
    }

    private static bool HasWord(string value, string word) => Regex.IsMatch(value,
        $@"(?:^|[^a-z0-9]){Regex.Escape(word)}(?:$|[^a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool Contains(string value, params string[] parts) =>
        parts.Any(part => value.Contains(part, StringComparison.OrdinalIgnoreCase));

    private static string First(IEnumerable<string> values) => values.Select(Clean).FirstOrDefault(value => value.Length > 0) ?? string.Empty;

    private static string Clean(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty :
        new string(value.Where(character => !char.IsControl(character)).Take(MaximumTextLength).ToArray()).Trim();

    private static Classification Advertised(string deviceType, string evidence) => new(deviceType, "Advertised", evidence);

    private sealed record Classification(string DeviceType, string Confidence, string Evidence);
}
