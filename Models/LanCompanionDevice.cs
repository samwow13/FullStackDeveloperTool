using FullStackLauncher.CompanionProtocol;

namespace FullStackLauncher.Models;

/// <summary>Transient LAN advertisement. Only a paired TLS connection authenticates this device.</summary>
public sealed record LanCompanionDevice(string Address, CompanionAdvertisement Advertisement, DateTimeOffset LastSeen)
{
    public string DeviceId => Advertisement.DeviceId;
    public string ComputerName => Advertisement.ComputerName;
    public string CertificateFingerprint => Advertisement.CertificateFingerprint;
    public bool PairingAvailable => Advertisement.PairingAvailable;
}
