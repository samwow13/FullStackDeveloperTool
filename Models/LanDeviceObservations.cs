namespace FullStackLauncher.Models;

/// <summary>Transient, unauthenticated information advertised by a device on the selected LAN.</summary>
public sealed record LanDeviceAdvertisement
{
    public string Address { get; init; } = string.Empty;
    public string Protocol { get; init; } = string.Empty;
    public string ServiceType { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string HostName { get; init; } = string.Empty;
    public string FriendlyName { get; init; } = string.Empty;
    public string Manufacturer { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string DeviceType { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> Properties { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Read-only results from one bounded discovery pass. Advertisements do not verify device identity.</summary>
public sealed class LanDeviceObservations
{
    public static LanDeviceObservations Empty { get; } = new(Array.Empty<LanDeviceAdvertisement>());

    public LanDeviceObservations(IReadOnlyList<LanDeviceAdvertisement> advertisements) => Advertisements = advertisements;

    public IReadOnlyList<LanDeviceAdvertisement> Advertisements { get; }
    public IEnumerable<string> Addresses => Advertisements.Select(item => item.Address).Distinct(StringComparer.Ordinal);
    public IReadOnlyList<LanDeviceAdvertisement> ForAddress(string address) =>
        Advertisements.Where(item => string.Equals(item.Address, address, StringComparison.Ordinal)).ToArray();
}
