namespace FullStackLauncher.Models;

/// <summary>
/// A transient description inferred from local network observations. Advertised names and model
/// information are supplied by the device and do not establish an authenticated remote identity.
/// </summary>
public sealed record LanDeviceIdentity
{
    public string DeviceType { get; init; } = "Unknown device";
    public string Manufacturer { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string FriendlyName { get; init; } = string.Empty;
    public string Confidence { get; init; } = "Unknown";
    public IReadOnlyList<string> Evidence { get; init; } = [];
    public IReadOnlyList<string> Services { get; init; } = [];
}
