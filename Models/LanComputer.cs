namespace FullStackLauncher.Models;

/// <summary>A currently connected IPv4 LAN and a bounded default scan range.</summary>
public sealed record LanNetwork(string Name, string LocalAddress, string Cidr, string SuggestedCidr)
{
    public string InterfaceId { get; init; } = string.Empty;
    public bool HasDefaultGateway { get; init; }
    public string GatewayAddress { get; init; } = string.Empty;
    public string DisplayName => $"{Name} · {LocalAddress} · {Cidr}";
}

/// <summary>
/// Observed LAN reachability and service availability. Open ports do not verify the operating system.
/// These observations and names are transient, unauthenticated discovery data, not credentials or trusted identities.
/// </summary>
public sealed record LanComputer
{
    public string Address { get; init; } = string.Empty;
    public string HostName { get; init; } = string.Empty;
    public string MacAddress { get; init; } = string.Empty;
    public bool IsLocal { get; init; }
    public bool IsOnline { get; init; }
    public bool PingAvailable { get; init; }
    public bool SmbAvailable { get; init; }
    public bool RdpAvailable { get; init; }
    public bool WinRmHttpAvailable { get; init; }
    public bool WinRmHttpsAvailable { get; init; }
    public bool WindowsVerified { get; init; }
    public string WindowsEvidence { get; init; } = "Operating system unknown";
    public LanDeviceIdentity Device { get; init; } = new();
    public string ReachabilityEvidence { get; init; } = string.Empty;
    public DateTimeOffset LastSeen { get; init; }
    public LanCompanionDevice? Companion { get; init; }
    public bool HasCompanion => Companion != null;
    public bool IsCompanionFresh => Companion != null && DateTimeOffset.UtcNow - Companion.LastSeen <= TimeSpan.FromSeconds(45);
    public string CompanionSummary => Companion == null ? string.Empty : IsCompanionFresh ? "Companion · Available" : "Companion · Unavailable";

    public string DisplayName => !string.IsNullOrWhiteSpace(Device.FriendlyName) ? Device.FriendlyName :
        !string.IsNullOrWhiteSpace(Companion?.ComputerName) ? Companion!.ComputerName :
        string.IsNullOrWhiteSpace(HostName) ? Address : HostName;
    public bool HasDeviceName => !string.Equals(DisplayName, Address, StringComparison.Ordinal);
    public string DeviceSummary => Device.Confidence == "Unknown" && Companion != null ? "Companion PC · Advertised" :
        Device.Confidence == "Unknown" ? Device.DeviceType : $"{Device.DeviceType} · {Device.Confidence}";
    public string DeviceModelSummary => string.Join(" · ", new[] { Device.Manufacturer, Device.Model }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase));
    public bool HasDeviceModel => !string.IsNullOrEmpty(DeviceModelSummary);
    public string IdentitySummary => $"{Address}{(string.IsNullOrEmpty(MacAddress) ? string.Empty : $" · {MacAddress}")}";
    public string ConnectionSummary
    {
        get
        {
            var services = new List<string>(4);
            if (Companion != null) services.Add(CompanionSummary);
            if (RdpAvailable) services.Add("Remote Desktop");
            if (SmbAvailable) services.Add("File sharing");
            if (WinRmHttpsAvailable) services.Add("WinRM HTTPS");
            if (WinRmHttpAvailable) services.Add("WinRM HTTP");
            return services.Count > 0 ? string.Join(" · ", services) : IsOnline ? "Online · no remote service detected" : "No response";
        }
    }
}

public sealed record LanDiscoveryProgress(int Completed, int Total, LanComputer? Computer);
