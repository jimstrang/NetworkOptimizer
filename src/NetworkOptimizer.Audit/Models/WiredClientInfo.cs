using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Audit.Models;

/// <summary>
/// Wrapper for a wired client with detection results for audit display.
/// </summary>
public class WiredClientInfo
{
    /// <summary>
    /// The UniFi client response data.
    /// </summary>
    public required UniFiClientResponse Client { get; init; }

    /// <summary>
    /// The network the client is connected to.
    /// </summary>
    public NetworkInfo? Network { get; init; }

    /// <summary>
    /// Device type detection result.
    /// </summary>
    public required DeviceDetectionResult Detection { get; init; }

    /// <summary>
    /// Name of the switch hosting the client port.
    /// </summary>
    public required string SwitchName { get; init; }

    /// <summary>
    /// MAC address of the switch hosting the client port.
    /// </summary>
    public string? SwitchMac { get; init; }

    /// <summary>
    /// Index of the switch port hosting the client.
    /// </summary>
    public required int PortIndex { get; init; }

    /// <summary>
    /// User-assigned switch port name.
    /// </summary>
    public string? PortName { get; init; }

    /// <summary>
    /// Display name for the client.
    /// </summary>
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Client.Name) ? Client.Name :
        !string.IsNullOrWhiteSpace(Client.Hostname) ? Client.Hostname :
        !string.IsNullOrWhiteSpace(Detection.ProductName) ? Detection.ProductName :
        Detection.Category != Core.Enums.ClientDeviceCategory.Unknown ? Detection.CategoryName :
        !string.IsNullOrWhiteSpace(Client.Mac) ? Client.Mac : "Unknown";

    /// <summary>
    /// MAC address of the client.
    /// </summary>
    public string? Mac => Client.Mac;
}
